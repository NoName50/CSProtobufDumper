// #define MapField // MapField does not exist in the striped Google.Protobuf.dll
// #define OneOf // It seems that OneOf does not exist in the game

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Remoting.Messaging;

namespace CSProtobufDumper
{
    public class MainApp
    {
        static bool flattenPackageFolders = false,
        verbose = false;

        static string outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "Output"),
        tabString = "  ";

        // Type Reference:
        // https://protobuf.dev/programming-guides/proto3/#scalar
        // https://protobuf.dev/programming-guides/encoding/#structure
        static readonly Dictionary<string, string> protoTypes = new Dictionary<string, string>
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
                        case "-f":
                        case "--flatten":
                            flattenPackageFolders = true;
                            break;
                        case "-h":
                        case "--help":
                            Console.WriteLine(@"CSharp Protobuf Dumper
A tool to extract .proto definitions from compiled C# assemblies that use Google.Protobuf
If no dll or folder is specified, all dlls in the current directory will be processed.

Usage: CSProtobufDumper [options] [dll/folder]...
Options:
  -f, --flatten        Flatten package folders in the output directory (default: false)
  -h, --help           Show help message and exit
  -i, --indent <num>   Specify the number of spaces for indentation (default: 2)
  -o, --output <dir>   Specify the output directory for generated .proto files (default: ./Output)
  -v, --verbose        Enable verbose output");
                            return;
                        case "-i":
                        case "--indent":
                            int indentIndex = Array.IndexOf(args, arg) + 1;
                            int.TryParse(args[indentIndex], out int tabCount);
                            if (indentIndex < args.Length && tabCount > 0)
                            {
                                tabString = new string(' ', tabCount);
                            }
                            else
                            {
                                Console.Error.WriteLine("Space count is not specified correctly after " + arg);
                                return;
                            }
                            break;

                        case "-o":
                        case "--output":
                            int outputIndex = Array.IndexOf(args, arg) + 1;
                            if (outputIndex < args.Length)
                            {
                                outputFolder = args[outputIndex];
                            }
                            else
                            {
                                Console.Error.WriteLine("Output folder is not specified after " + arg);
                                return;
                            }
                            break;
                        case "-v":
                        case "--verbose":
                            verbose = true;
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
            CallContext.LogicalSetData("options", new object[] { flattenPackageFolders, outputFolder, tabString, verbose });
            Stopwatch stopwatch = Stopwatch.StartNew();
            foreach (string dll in inputPaths)
            {
                AppDomain domain = AppDomain.CreateDomain("TempDomain");
                CallContext.LogicalSetData("assemblyPath", dll);
                domain.DoCallBack(() =>
                {
                    object[] options = (object[])CallContext.LogicalGetData("options");
                    flattenPackageFolders = (bool)options[0];
                    outputFolder = (string)options[1];
                    tabString = (string)options[2];
                    verbose = (bool)options[3];
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
            Console.WriteLine($"Processing assembly: {assembly.GetName().Name}");
            if (assembly.GetReferencedAssemblies().Any(a => a.Name == "Google.Protobuf"))
            {
                Directory.CreateDirectory(outputFolder);
                List<Type> necessaryEnumTypes = new List<Type>();
                #region DumpMessage
                foreach (Type type in assembly.GetTypes().Where(t => t.IsClass && t.GetInterface("Google.Protobuf.IMessage") != null))
                {
                    if (verbose) Console.WriteLine($"Found message class: {type.FullName} in assembly {assembly.GetName().Name}");
                    IndentedTextWriter outputFile = InitializeFile(assemblyPath, type);
                    #region ParseMessage
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
                                if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition().FullName == "Google.Protobuf.Collections.RepeatedField`1" && protoMessage.fieldList.Any(f => f.fieldNumber == fieldNumber))
                                {
                                    // Handle the case where a repeated field has two tags (one for type `repeated` itself with wire type 2 and one for data)
                                    if (wireTypeIndex != 2)
                                    {
                                        // If the first tag is not for data, reparse the data tag and correct the type
                                        protoMessage.fieldList.First(f => f.fieldNumber == fieldNumber).fieldType = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName);
                                    }
                                    // Skip the second tag for itself
                                    return;
                                }
                                if (verbose) Console.WriteLine($"\tFound Prop {fieldNumber}: {prop.Name} ({prop.PropertyType.Name})");
                                ProtoField protoField = new ProtoField
                                {
                                    fieldName = prop.Name,
                                    fieldType = ParseType(prop.PropertyType.FullName)
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
                                        protoField.fieldType = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[0]);
                                    }
#if MapField
                                    else if (genericTypeFullName == "Google.Protobuf.Collections.MapField`2")
                                    {
                                        protoField.mapKey = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName);
                                        protoField.mapValue = ParseType(prop.PropertyType.GetGenericArguments()[1].FullName);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[0]);
                                        AddImportIfNeed(prop.PropertyType.GetGenericArguments()[1]);
                                    }
#endif
                                }
                                protoField.fieldNumber = fieldNumber;
                                protoMessage.fieldList.Add(protoField);

                                string ParseType(string typeFullName)
                                {
                                    return protoTypes.TryGetValue($"{typeFullName},{wireTypeIndex}", out string proto) ? proto : typeFullName;
                                }

                                void AddImportIfNeed(Type propType)
                                {
                                    if (type != propType && (propType.IsEnum || (propType.IsClass && propType.GetInterface("Google.Protobuf.IMessage") != null)))
                                    {
                                        protoMessage.importTypeList.Add(propType);
                                    }
                                }
                            }
                        }
                    }
                    #endregion
                    #region WriteImport
                    foreach (Type importType in protoMessage.importTypeList.Distinct())
                    {
                        outputFile.WriteLine($"import \"{(flattenPackageFolders ? importType.FullName : $"{importType.Namespace}/{importType.Name}")}.proto\";");
                    }
                    if (protoMessage.importTypeList.Count > 0)
                    {
                        outputFile.WriteLine();
                    }
                    #endregion
                    #region WriteMessage
                    outputFile.WriteLine($"message {protoMessage.protoName} {{");
                    WriteField(protoMessage.fieldList);
#if OneOf
                    outputFile.Indent++;
                    foreach (OneOf oneOf in protoMessage.oneOfList)
                    {
                        outputFile.WriteLine($"oneof {oneOf.oneOfName} {{");
                        WriteField(oneOf.fieldList);
                        outputFile.WriteLine("}");
                    }
                    outputFile.Indent--;
#endif
                    outputFile.Write("}");

                    void WriteField(List<ProtoField> fields)
                    {
                        outputFile.Indent++;
                        foreach (ProtoField field in fields)
                        {
                            if (field.isRepeated)
                            {
                                outputFile.WriteLine($"repeated {field.fieldType} {field.fieldName.PascalToSnake()} = {field.fieldNumber};");
                            }
#if MapField
                            else if (field.mapKey != null)
                            {
                                outputFile.WriteLine($"map<{field.mapKey},{field.mapValue}> {field.fieldName.PascalToSnake()} = {field.fieldNumber};");
                            }
#endif
                            else
                            {
                                outputFile.WriteLine($"{field.fieldType} {field.fieldName.PascalToSnake()} = {field.fieldNumber};");
                            }
                        }
                        outputFile.Indent--;
                    }
                    #endregion
                    outputFile.Close();
                    protoCount++;
                }
                #endregion
                #region DumpEnum
                foreach (Type enumType in necessaryEnumTypes.Distinct())
                {
                    if (verbose) Console.WriteLine($"Found enum type: {enumType.FullName} in assembly {assembly.GetName().Name}");
                    IndentedTextWriter outputFile = InitializeFile(assemblyPath, enumType);
                    #region ParseEnum
                    ProtoEnum protoEnum = new ProtoEnum
                    {
                        enumName = enumType.Name
                    };
                    foreach (var value in Enum.GetValues(enumType))
                    {
                        if (verbose) Console.WriteLine($"\tFound enum value: {value} = {Convert.ToInt32(value)}");
                        protoEnum.valDict[enumType.Name + "_" + value.ToString()] = Convert.ToInt32(value);
                    }
                    #endregion
                    #region WriteEnum
                    outputFile.WriteLine($"enum {protoEnum.enumName} {{");
                    outputFile.Indent++;
                    foreach (KeyValuePair<string, int> entry in protoEnum.valDict)
                    {
                        outputFile.WriteLine($"{entry.Key} = {entry.Value};");
                    }
                    outputFile.Indent--;
                    outputFile.Write("}");
                    #endregion
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

        static IndentedTextWriter InitializeFile(string assemblyPath, Type type)
        {
            string outputPath;
            if (flattenPackageFolders)
            {
                outputPath = Path.Combine(outputFolder, $"{type.FullName}.proto");
            }
            else
            {
                string namespacePath = Path.Combine(outputFolder, type.Namespace);
                outputPath = Path.Combine(namespacePath, $"{type.Name}.proto");
                Directory.CreateDirectory(namespacePath);
            }
            IndentedTextWriter outputFile = new IndentedTextWriter(new StreamWriter(outputPath), "  ");
            outputFile.WriteLine($"// Extracted from {Path.GetFileName(assemblyPath)}");
            outputFile.WriteLine("syntax = \"proto3\";\n");
            outputFile.WriteLine($"package {type.Namespace};\n");
            outputFile.WriteLine($"option csharp_namespace = \"{type.Namespace}\";\n");
            return outputFile;
        }
    }
}