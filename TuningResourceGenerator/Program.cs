using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Destrospean.TuningResourceGenerator
{
    class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                return;
            }
            var package = s3pi.Package.Package.OpenPackage(0, args[0], true);
            var nameMapResourceIndexEntries = package.FindAll(x => x.ResourceType == 0x166038C);
            var nameMapResource = new NameMapResource.NameMapResource(0, nameMapResourceIndexEntries.Count == 0 ? null : ((s3pi.Interfaces.APackage)package).GetResource(nameMapResourceIndexEntries[0]));
            foreach (var resourceIndexEntry in package.FindAll(x => x.ResourceType == 0x73FAA07))
            {
                foreach (var type in AssemblyDefinition.ReadAssembly(new ScriptResource.ScriptResource(0, ((s3pi.Interfaces.APackage)package).GetResource(resourceIndexEntry)).Assembly.BaseStream).MainModule.GetTypes())
                {
                    var fields = Array.FindAll(type.Fields.ToArray(), x => Array.Exists(x.CustomAttributes.ToArray(), y => y.AttributeType.Name == "Tunable" || y.AttributeType.Name == "TunableAttribute"));
                    var tunableComments = Array.ConvertAll(fields, x =>
                        {
                            var index = Array.FindIndex(x.CustomAttributes.ToArray(), y => y.AttributeType.Name == "TunableComment" || y.AttributeType.Name == "TunableCommentAttribute");
                            return index == -1 ? null : " " + x.CustomAttributes[index].ConstructorArguments[0].Value.ToString().Trim(' ') + " ";
                        });
                    var hasTunableComments = !Array.TrueForAll(tunableComments, x => x == null);
                    var document = new System.Xml.XmlDocument
                        {
                            PreserveWhitespace = hasTunableComments
                        };
                    document.LoadXml("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<base>\r\n  <Current_Tuning></Current_Tuning>\r\n</base>");
                    var currentTuningNode = document.SelectSingleNode("base/Current_Tuning");
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];
                        if (tunableComments[i] != null)
                        {
                            var tunableComment = document.CreateComment(tunableComments[i]);
                            currentTuningNode.AppendChild(tunableComment);
                            currentTuningNode.InsertBefore(document.CreateSignificantWhitespace("\r\n    "), tunableComment);
                        }
                        object initialValue = null;
                        var instructionsByField = new Dictionary<string, List<Instruction>>(); 
                        var staticConstructorIndex = Array.FindIndex(type.Methods.ToArray(), y => y.IsConstructor && y.IsStatic);
                        if (staticConstructorIndex > -1)
                        {
                            var tempInstructions = new List<Instruction>();
                            foreach (var instruction in type.Methods[staticConstructorIndex].Body.Instructions)
                            {
                                if (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld)
                                {
                                    instructionsByField.Add(((FieldReference)instruction.Operand).Name, new List<Instruction>(tempInstructions));
                                    tempInstructions.Clear();
                                    continue;
                                }
                                tempInstructions.Add(instruction);
                            }
                        }
                        List<Instruction> instructions;
                        if (instructionsByField.TryGetValue(field.Name, out instructions))
                        {
                            if (instructions.Count == 1)
                            {
                                initialValue = instructions[0].Operand;
                            }
                            if (instructions.Count > 1 && instructions[1].OpCode == OpCodes.Newarr)
                            {
                                instructions.RemoveRange(0, 4);
                                var arrayString = "";
                                for (var j = 0; j < instructions.Count; j++)
                                {
                                    if (instructions[j].OpCode.ToString().StartsWith("stelem"))
                                    {
                                        break;
                                    }
                                    if ((j & 1) == 1)
                                    {
                                        arrayString += instructions[j].Operand.ToString() + ",";
                                    }
                                }
                                initialValue = arrayString.TrimEnd(',');
                            }
                        }
                        var tunableElement = document.CreateElement(field.Name);
                        tunableElement.SetAttribute("value", initialValue?.ToString() ?? (field.FieldType.Name == "Boolean" ? "False" : field.FieldType.Name == "String" ? "" : "0"));
                        currentTuningNode.AppendChild(tunableElement);
                        if (hasTunableComments)
                        {
                            currentTuningNode.InsertBefore(document.CreateSignificantWhitespace("\r\n    "), tunableElement);
                            currentTuningNode.InsertAfter(document.CreateSignificantWhitespace(i == fields.Length - 1 ? "\r\n  " : "\r\n"), tunableElement);
                        }
                    }
                    if (fields.Length > 0)
                    {
                        var stream = new System.IO.MemoryStream();
                        var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
                            {
                                Indent = true,
                                NewLineChars = "\r\n"
                            });
                        document.Save(writer);
                        var instance = System.Security.Cryptography.FNV64.GetHash(type.FullName);
                        if (nameMapResource.ContainsKey(instance))
                        {
                            nameMapResource.Remove(instance);
                        }
                        nameMapResource.Add(instance, type.FullName);
                        var matchingTuningResourceIndexEntries = package.FindAll(x => x.ResourceType == 0x333406C && x.ResourceGroup == 0 && x.Instance == instance);
                        if (matchingTuningResourceIndexEntries.Count > 0)
                        {
                            package.DeleteResource(matchingTuningResourceIndexEntries[0]);
                        }
                        package.AddResource(new ResourceKey(0x333406C, 0, instance), stream, true);
                    }
                }
            }
            if (nameMapResourceIndexEntries.Count > 0)
            {
                package.DeleteResource(nameMapResourceIndexEntries[0]);
            }
            package.AddResource(new ResourceKey(0x166038C, nameMapResourceIndexEntries.Count == 0 ? 0 : nameMapResourceIndexEntries[0].ResourceGroup, nameMapResourceIndexEntries.Count == 0 ? 0 : nameMapResourceIndexEntries[0].Instance), nameMapResource.Stream, true);
            package.SavePackage();
        }
    }
}
