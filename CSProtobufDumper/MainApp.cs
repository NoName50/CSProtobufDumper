using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Remoting.Messaging;
using System.Text.RegularExpressions;

namespace CSProtobufDumper
{
    public class MainApp
    {
        static bool Verbose = false;

        static string OutputFolder = Path.Combine(Directory.GetCurrentDirectory(), "Output");

        // Type Reference:
        // https://protobuf.dev/programming-guides/proto3/#scalar
        // https://protobuf.dev/programming-guides/encoding/#structure
        static readonly Dictionary<string, string> ProtoTypes = new Dictionary<string, string>
        {
            ["System.Double,1"] = "double",
            ["System.Single,5"] = "float",
            ["System.Int32,0"] = "int32",
            ["System.Int64,0"] = "int64",
            ["System.UInt32,0"] = "uint32",
            ["System.UInt64,0"] = "uint64",
            ["System.UInt32,5"] = "fixed32",
            ["System.UInt64,1"] = "fixed64",
            ["System.Int32,5"] = "sfixed32",
            ["System.Int64,1"] = "sfixed64",
            ["System.Boolean,0"] = "bool",
            ["System.String,2"] = "string",
            ["Google.Protobuf.ByteString,2"] = "bytes"
        };

        static string ParseType(string type, byte wireTypeIndex)
        {
            return ProtoTypes.TryGetValue($"{type},{wireTypeIndex}", out string proto) ? proto : type;
        }

        static void Main(string[] args)
        {
            List<string> inputPaths = new List<string>();
            uint protoCount = 0;
            foreach (string arg in args)
            {
                if (arg.StartsWith("-"))
                {
                    switch (arg)
                    {
                        case "-h":
                        case "--help":
                            Console.WriteLine(@"CSharp Protobuf Dumper
A tool to extract .proto definitions from compiled C# assemblies that use Google.Protobuf
If no dll or folder is specified, all dlls in the current directory will be processed.

Usage: CSProtobufDumper [options] [dll/folder]...
Options:
  -h, --help\tShow this help message and exit
  -o, --output <folder>\tSpecify output folder for generated .proto files (default: ./Output)
  -v, --verbose\tEnable verbose output");
                            return;
                        case "-o":
                        case "--output":
                            int outputIndex = Array.IndexOf(args, arg) + 1;
                            if (outputIndex < args.Length)
                            {
                                OutputFolder = args[outputIndex];
                            }
                            else
                            {
                                Console.Error.WriteLine("Output folder not specified after " + arg);
                                return;
                            }
                            break;
                        case "-v":
                        case "--verbose":
                            Verbose = true;
                            break;
                    }
                }
                else if (File.Exists(arg) && arg.EndsWith(".dll"))
                {
                    inputPaths.Add(arg);
                }
                else if (Directory.Exists(arg))
                {
                    inputPaths.AddRange(Directory.GetFiles(arg, "*.dll", SearchOption.TopDirectoryOnly));
                }
            }
            if (inputPaths.Count == 0)
            {
                inputPaths.AddRange(Directory.GetFiles(Directory.GetCurrentDirectory(), "*.dll", SearchOption.TopDirectoryOnly));
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            foreach (string dll in inputPaths)
            {
                AppDomain domain = AppDomain.CreateDomain("TempDomain");
                CallContext.LogicalSetData("assemblyPath", dll);
                domain.DoCallBack(() =>
                {
                    uint assemblyProtoCount = 0;
                    Dump((string)CallContext.LogicalGetData("assemblyPath"), ref assemblyProtoCount);
                    CallContext.LogicalSetData("assemblyProtoCount", assemblyProtoCount);
                });
                protoCount += (uint)CallContext.LogicalGetData("assemblyProtoCount");
                AppDomain.Unload(domain);
                GC.Collect();
            }
            stopwatch.Stop();
            Console.WriteLine($"Done! Generated {protoCount} .proto files in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        }

        static void Dump(string assemblyPath, ref uint protoCount)
        {
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            Console.WriteLine($"Processing assembly: {assembly.FullName}");
            if (assembly.GetReferencedAssemblies().Any(a => a.Name == "Google.Protobuf"))
            {
                Directory.CreateDirectory(OutputFolder);
                List<Type> necessaryEnumTypes = new List<Type>();
                #region DumpMessage
                foreach (Type type in assembly.GetTypes().Where(t => t.IsClass && t.GetInterfaces().Any(i => i.FullName == "Google.Protobuf.IMessage")))
                {
                    if (Verbose) Console.WriteLine($"Found message class: {type.FullName} in assembly {assembly.GetName().Name}");
                    string outputPath = Path.Combine(OutputFolder, $"{type.FullName}.proto");
                    StreamWriter outputFile = new StreamWriter(outputPath);
                    outputFile.WriteLine($"// Extracted from {Path.GetFileName(assemblyPath)}");
                    outputFile.WriteLine("syntax = \"proto3\";\n");
                    outputFile.WriteLine($"package {type.Namespace};\n");
                    List<string> importTypes = new List<string>();
                    ProtoMessage protoMessage = new ProtoMessage
                    {
                        protoName = type.Name
                    };
                    List<FieldInfo> fieldInfoList = new List<FieldInfo>();
                    byte[] ilBytes;
                    try
                    {
                        ilBytes = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).First(method => method.Name == "MergeFrom" && method.GetParameters()[0].ParameterType.FullName == "Google.Protobuf.CodedInputStream")?.GetMethodBody()?.GetILAsByteArray();
                    }
                    catch (InvalidOperationException)
                    {
                        Console.Error.WriteLine($"Cannot find the MergeFrom(CodedInputStream) method for {type.FullName}. This message will be skipped.");
                        continue;
                    }
                    PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    for (int i = 0; i < ilBytes.Length; i++)
                    {
                        if (ilBytes[i] == OpCodes.Ldloc_0.Value)
                        {
                            int cursor = i + 1;
                            uint fieldNumber; byte wireTypeIndex;
                            PropertyInfo prop;
                            if (ilBytes[cursor] == OpCodes.Ldc_I4_8.Value)
                            {
                                cursor++;
                                fieldNumber = 1; wireTypeIndex = 0;
                                ProcessBEQ();
                                ParseProp();
                            }
                            else if (ilBytes[cursor] == OpCodes.Ldc_I4_S.Value)
                            {
                                cursor++;
                                ProcessTag(ilBytes[cursor]);
                                cursor++;
                                ProcessBEQ();
                                ParseProp();
                            }
                            else if (ilBytes[cursor] == OpCodes.Ldc_I4.Value)
                            {
                                cursor++;
                                ProcessTag(BitConverter.ToUInt32(ilBytes, cursor));
                                cursor += sizeof(int);
                                ProcessBEQ();
                                ParseProp();
                            }

                            void ProcessTag(uint tag)
                            {
                                fieldNumber = tag >> 3;
                                wireTypeIndex = (byte)(tag & 7);
                            }

                            void ProcessBEQ()
                            {
                                if (ilBytes[cursor] == OpCodes.Beq.Value)
                                {
                                    cursor++;
                                    cursor += BitConverter.ToInt32(ilBytes, cursor) + sizeof(int);
                                }
                                else if (ilBytes[cursor] == OpCodes.Beq_S.Value)
                                {
                                    cursor++;
                                    cursor += (sbyte)ilBytes[cursor] + sizeof(sbyte);
                                }
                            }

                            void ParseProp()
                            {
                                if (ilBytes[cursor] == OpCodes.Ldarg_0.Value)
                                {
                                    cursor++;
                                    if (ilBytes[cursor] == OpCodes.Ldarg_1.Value && ilBytes[++cursor] == OpCodes.Callvirt.Value)
                                    {
                                        int callIndex = -1;
                                        for (int j = cursor; j < ilBytes.Length; j++)
                                        {
                                            if (ilBytes[j] == OpCodes.Call.Value)
                                            {
                                                callIndex = j;
                                                break;
                                            }
                                            else if (ilBytes[j] == OpCodes.Br.Value || ilBytes[j] == OpCodes.Br_S.Value)
                                            {
                                                break;
                                            }
                                        }
                                        if (callIndex == -1)
                                        {
                                            Console.Error.WriteLine($"Cannot find the setter call op index of property for {type.FullName}. This field will be skipped.");
                                            return;
                                        }
                                        cursor = callIndex + 1;
                                        int metadataToken = BitConverter.ToInt32(ilBytes, cursor);
                                        try
                                        {
                                            prop = properties.First(p => p.SetMethod?.MetadataToken == metadataToken);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            Console.Error.WriteLine($"Cannot find the setter({metadataToken}) of property for {type.FullName}. This field will be skipped.");
                                            return;
                                        }
                                        ProcessProp();
                                    }
                                    else if (ilBytes[cursor] == OpCodes.Ldfld.Value)
                                    {
                                        cursor++;
                                        int fieldMetadataToken = BitConverter.ToInt32(ilBytes, cursor);
                                        try
                                        {
                                            FieldInfo fieldInfo = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).First(f => f.MetadataToken == fieldMetadataToken);
                                            prop = properties.First(p => fieldInfo.Name.ToLower().StartsWith(p.Name.ToLower()) && p.PropertyType == fieldInfo.FieldType);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            Console.Error.WriteLine($"Cannot find the corresponding prop(field token: {fieldMetadataToken}) for {type.FullName}. This field will be skipped.");
                                            return;
                                        }
                                        ProcessProp();
                                    }
                                }
                            }

                            void ProcessProp()
                            {
                                if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition().FullName == "Google.Protobuf.Collections.RepeatedField`1" && protoMessage.fieldList.Any(f => f.val == fieldNumber))
                                {
                                    // Handle the case where a repeated field has two tags (one for type `repeated` itself with wire type 2 and one for data)
                                    if (wireTypeIndex != 2)
                                    {
                                        // If the first tag is not for data, reparse the data tag and correct the type
                                        protoMessage.fieldList.First(f => f.val == fieldNumber).fieldType = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName, wireTypeIndex);
                                    }
                                    // Skip the second tag for itself
                                    return;
                                }
                                if (Verbose) Console.WriteLine($"\tFound Prop {fieldNumber}: {prop.Name} ({prop.PropertyType.Name})");
                                ProtoField protoField = new ProtoField
                                {
                                    fieldName = prop.Name,
                                    fieldType = ParseType(prop.PropertyType.FullName, wireTypeIndex)
                                };
                                AddImportIfNeed(prop.PropertyType);
                                if (prop.PropertyType.IsEnum)
                                {
                                    necessaryEnumTypes.Add(prop.PropertyType);
                                }
                                else if (prop.PropertyType.IsGenericType)
                                {
                                    string genericTypeFullName = prop.PropertyType.GetGenericTypeDefinition().FullName;
                                    if (genericTypeFullName == "Google.Protobuf.Collections.RepeatedField`1")
                                    {
                                        protoField.isRepeated = true;
                                        protoField.fieldType = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName, wireTypeIndex);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[0]);
                                    }
                                    else if (genericTypeFullName == "Google.Protobuf.Collections.MapField`2")
                                    {
                                        protoField.mapKey = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName, wireTypeIndex);
                                        protoField.mapValue = ParseType(prop.PropertyType.GetGenericArguments()[1].FullName, wireTypeIndex);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[0]);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[1]);
                                    }
                                }
                                protoField.val = fieldNumber;
                                protoMessage.fieldList.Add(protoField);

                                void AddImportIfNeed(Type propType)
                                {
                                    if (type != propType && (propType.IsEnum || (propType.IsClass && propType.GetInterfaces().Any(i2 => i2.FullName == "Google.Protobuf.IMessage"))))
                                    {
                                        importTypes.Add(propType.FullName);
                                    }
                                }
                            }
                        }
                    }
                    foreach (string importType in importTypes.Distinct())
                    {
                        outputFile.WriteLine($"import \"{importType}.proto\";");
                    }
                    if (importTypes.Count > 0)
                    {
                        outputFile.WriteLine();
                    }
                    outputFile.WriteLine($"option csharp_namespace = \"{type.Namespace}\";\n");
                    WriteMessageToFile(protoMessage, outputFile);
                    outputFile.Close();
                    protoCount++;
                }
                #endregion
                #region DumpEnum
                foreach (Type enumType in necessaryEnumTypes.Distinct())
                {
                    if (Verbose) Console.WriteLine($"Found enum type: {enumType.FullName} in assembly {assembly.GetName().Name}");
                    string outputPath = Path.Combine(OutputFolder, $"{enumType.FullName}.proto");
                    StreamWriter outputFile = new StreamWriter(outputPath);
                    outputFile.WriteLine($"// Extracted from {Path.GetFileName(assemblyPath)}");
                    outputFile.WriteLine("syntax = \"proto3\";\n");
                    outputFile.WriteLine($"package {enumType.Namespace};\n");
                    outputFile.WriteLine($"option csharp_namespace = \"{enumType.Namespace}\";\n");
                    ProtoEnum protoEnum = new ProtoEnum
                    {
                        enumName = enumType.Name
                    };
                    foreach (var value in Enum.GetValues(enumType))
                    {
                        if (Verbose) Console.WriteLine($"\tFound enum value: {value} = {Convert.ToInt32(value)}");
                        protoEnum.valDict[enumType.Name + "_" + value.ToString()] = Convert.ToInt32(value);
                    }
                    WriteEnumToFile(protoEnum, outputFile, new List<string>());
                    outputFile.Close();
                    protoCount++;
                }
                #endregion
            }
            else
            {
                Console.WriteLine($"Skipping assembly {assembly.GetName().Name} as it does not reference Google.Protobuf");
            }
        }

        static string CamelToSnake(string camelStr)
        {
            bool isAllUppercase = camelStr.All(char.IsUpper); // Beebyte
            if (string.IsNullOrEmpty(camelStr) || isAllUppercase)
                return camelStr;
            return Regex.Replace(camelStr, @"(([a-z])(?=[A-Z][a-zA-Z])|([A-Z])(?=[A-Z][a-z]))", "$1_").ToLower();
        }

        static void WriteMessageToFile(ProtoMessage msg, StreamWriter writer)
        {
            writer.WriteLine($"message {msg.protoName} {{");
            foreach (ProtoField field in msg.fieldList)
            {
                if (field.isRepeated)
                {
                    writer.WriteLine($"  repeated {field.fieldType} {CamelToSnake(field.fieldName)} = {field.val};");
                }
                else if (field.mapKey != null)
                {
                    writer.WriteLine($"  map<{field.mapKey},{field.mapValue}> {CamelToSnake(field.fieldName)} = {field.val};");
                }
                else
                {
                    writer.WriteLine($"  {field.fieldType} {CamelToSnake(field.fieldName)} = {field.val};");
                }
            }
            foreach (OneOf oneOf in msg.oneOfList)
            {
                writer.WriteLine($"  oneof {oneOf.oneOfName} {{");
                foreach (ProtoField field in oneOf.fieldList)
                {
                    if (field.isRepeated)
                    {
                        writer.WriteLine($"    repeated {field.fieldType} {CamelToSnake(field.fieldName)} = {field.val};");
                    }
                    else if (field.mapKey != null)
                    {
                        writer.WriteLine($"    map<{field.mapKey},{field.mapValue}> {CamelToSnake(field.fieldName)} = {field.val};");
                    }
                    else
                    {
                        writer.WriteLine($"    {field.fieldType} {CamelToSnake(field.fieldName)} = {field.val};");
                    }
                }
                writer.WriteLine($"  }}");
            }
            writer.Write("}");
        }

        static void WriteEnumToFile(ProtoEnum msg, StreamWriter writer, List<string> blackListEnumNames)
        {
            if (blackListEnumNames.Contains(msg.enumName))
            {
                return;
            }
            writer.WriteLine($"enum {msg.enumName} {{");
            foreach (KeyValuePair<string, int> entry in msg.valDict)
            {
                writer.WriteLine($"  {entry.Key} = {entry.Value};");
            }
            writer.WriteLine("}\n");
        }
    }
}