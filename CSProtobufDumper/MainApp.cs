using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text.RegularExpressions;

namespace CSProtobufDumper
{
    public class MainApp
    {
        static bool Verbose = false;
        static string OutputFolder = Path.Combine(Directory.GetCurrentDirectory(), "Output");
        static readonly Dictionary<string, string> ProtoTypes = new Dictionary<string, string>
        {
            ["System.UInt32"] = "uint32",
            ["System.UInt64"] = "uint64",
            ["System.Boolean"] = "bool",
            ["System.Int32"] = "int32",
            ["System.Int64"] = "int64",
            ["System.String"] = "string",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["Google.Protobuf.ByteString"] = "bytes"
        };

        static string ParseType(string type)
        {
            return ProtoTypes.TryGetValue(type, out var proto) ? proto : type;
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
                        uint propIndex = 0;
                        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
                        {
                            if (Verbose) Console.WriteLine($"\tFound Prop #{propIndex + 1}: {prop.Name} ({prop.PropertyType.Name})");
                            ProtoFIeld protoField = new ProtoFIeld
                            {
                                fieldName = prop.Name,
                                fieldType = ParseType(prop.PropertyType.FullName)
                            };
                            AddImportIfNeed(importTypes, type, prop.PropertyType);
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
                                    AddImportIfNeed(importTypes, type, prop.PropertyType.GetGenericArguments()[0]);
                                }
                                else if (genericTypeFullName == "Google.Protobuf.Collections.MapField`2")
                                {
                                    protoField.mapKey = ParseType(prop.PropertyType.GetGenericArguments()[0].FullName);
                                    protoField.mapValue = ParseType(prop.PropertyType.GetGenericArguments()[1].FullName);
                                    AddImportIfNeed(importTypes, type, prop.PropertyType.GetGenericArguments()[0]);
                                    AddImportIfNeed(importTypes, type, prop.PropertyType.GetGenericArguments()[1]);
                                }
                            }
                            protoField.val = ++propIndex;
                            protoMessage.fieldList.Add(protoField);
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
            else if (Verbose)
            {
                Console.WriteLine($"Skipping assembly {Path.GetFileName(assemblyPath)} ({assembly.GetName().Name}) as it does not reference Google.Protobuf");
            }
        }


        static void AddImportIfNeed(List<string> importTypes, Type thisType, Type propType)
        {
            if (thisType != propType && (propType.IsEnum || (propType.IsClass && propType.GetInterfaces().Any(i => i.FullName == "Google.Protobuf.IMessage"))))
            {
                importTypes.Add(propType.FullName);
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
            foreach (ProtoFIeld field in msg.fieldList)
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
                foreach (ProtoFIeld field in oneOf.fieldList)
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