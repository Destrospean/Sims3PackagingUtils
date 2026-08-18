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

            // Load the package
            var package = s3pi.Package.Package.OpenPackage(0, args[0], true);

            // Fetch the _KEY resource, or create a new one if one doesn't exist
            var nameMapResourceIndexEntries = package.FindAll(x => x.ResourceType == 0x166038C);
            var nameMapResource = new NameMapResource.NameMapResource(0, nameMapResourceIndexEntries.Count == 0 ? null : ((s3pi.Interfaces.APackage)package).GetResource(nameMapResourceIndexEntries[0]));

            // Iterate through the assembly resources to get any classes with tunable fields
            foreach (var resourceIndexEntry in package.FindAll(x => x.ResourceType == 0x73FAA07))
            {
                // Iterate through the classes with tunable fields
                foreach (var type in AssemblyDefinition.ReadAssembly(new ScriptResource.ScriptResource(0, ((s3pi.Interfaces.APackage)package).GetResource(resourceIndexEntry)).Assembly.BaseStream).MainModule.GetTypes())
                {
                    // Fetch all the tunable fields of the current class
                    var tunableFields = Array.FindAll(type.Fields.ToArray(), x => Array.Exists(x.CustomAttributes.ToArray(), y => y.AttributeType.Name == "Tunable" || y.AttributeType.Name == "TunableAttribute"));

                    // Fetch the comments for all tunable fields (as strings; null for each tunable field that does not have a comment)
                    var tunableComments = Array.ConvertAll(tunableFields, x =>
                        {
                            var index = Array.FindIndex(x.CustomAttributes.ToArray(), y => y.AttributeType.Name == "TunableComment" || y.AttributeType.Name == "TunableCommentAttribute");
                            return index == -1 ? null : " " + x.CustomAttributes[index].ConstructorArguments[0].Value.ToString().Trim(' ') + " ";
                        });
                    
                    var hasTunableComments = !Array.TrueForAll(tunableComments, x => x == null);

                    // Create the XmlDocument object and load the template for tuning XMLs
                    var xmlDocument = new System.Xml.XmlDocument
                        {
                            PreserveWhitespace = hasTunableComments
                        };
                    xmlDocument.LoadXml("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<base>\r\n  <Current_Tuning></Current_Tuning>\r\n</base>");

                    var currentTuningNode = xmlDocument.SelectSingleNode("base/Current_Tuning");

                    // Check if a static constructor exists and group the instructions within said constructor by field name
                    var instructionsByFieldName = new Dictionary<string, List<Instruction>>();
                    var staticConstructorIndex = Array.FindIndex(type.Methods.ToArray(), y => y.IsConstructor && y.IsStatic);
                    if (staticConstructorIndex > -1)
                    {
                        var tempInstructions = new List<Instruction>();
                        foreach (var instruction in type.Methods[staticConstructorIndex].Body.Instructions)
                        {
                            if (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld)
                            {
                                instructionsByFieldName.Add(((FieldReference)instruction.Operand).Name, new List<Instruction>(tempInstructions));
                                tempInstructions.Clear();
                                continue;
                            }
                            tempInstructions.Add(instruction);
                        }
                    }

                    // Iterate through each of the tunable fields of the current class
                    for (var i = 0; i < tunableFields.Length; i++)
                    {
                        var field = tunableFields[i];

                        // Check if there is a tunable comment for this field and add it to the XML if so
                        if (tunableComments[i] != null)
                        {
                            var tunableComment = xmlDocument.CreateComment(tunableComments[i]);
                            currentTuningNode.AppendChild(tunableComment);
                            currentTuningNode.InsertBefore(xmlDocument.CreateSignificantWhitespace("\r\n    "), tunableComment);
                        }

                        object initialValue = null;

                        // Fetch any values assigned in the code to the tunable field
                        List<Instruction> instructions;
                        if (instructionsByFieldName.TryGetValue(field.Name, out instructions))
                        {
                            // Fetch the primitive value of the field
                            if (instructions.Count == 1)
                            {
                                initialValue = instructions[0].Operand ?? instructions[0].OpCode == OpCodes.Ldc_I4_1;
                            }

                            // Fetch the array value of the field
                            if (instructions.Count > 1 && instructions[1].OpCode == OpCodes.Newarr)
                            {
                                // Remove the first four instructions as they are irrelevant (since we have already determined we are dealing with an array)
                                instructions.RemoveRange(0, 4);

                                var arrayString = "";
                                for (var j = 0; j < instructions.Count; j++)
                                {
                                    if (instructions[j].OpCode.ToString().StartsWith("stelem"))
                                    {
                                        break;
                                    }

                                    // Get only the odd-numbered instructions (the ones that hold the elements), as the even-numbered ones are for the indices
                                    if ((j & 1) == 1)
                                    {
                                        arrayString += instructions[j].Operand.ToString() + ",";
                                    }
                                }
                                initialValue = arrayString.TrimEnd(',');
                            }
                        }

                        // Create and add the tunable field as an element in the XML
                        var tunableElement = xmlDocument.CreateElement(field.Name);
                        tunableElement.SetAttribute("value", initialValue?.ToString() ?? (field.FieldType.Name == "Boolean" ? "False" : field.FieldType.Name == "String" || field.FieldType.IsArray ? "" : "0"));
                        currentTuningNode.AppendChild(tunableElement);

                        // Do formatting for XMLs with comments
                        if (hasTunableComments)
                        {
                            currentTuningNode.InsertBefore(xmlDocument.CreateSignificantWhitespace("\r\n    "), tunableElement);
                            currentTuningNode.InsertAfter(xmlDocument.CreateSignificantWhitespace(i == tunableFields.Length - 1 ? "\r\n  " : "\r\n"), tunableElement);
                        }
                    }
                    if (tunableFields.Length > 0)
                    {
                        // Create the XML stream and writer
                        var xmlStream = new System.IO.MemoryStream();
                        var xmlWriter = System.Xml.XmlWriter.Create(xmlStream, new System.Xml.XmlWriterSettings
                            {
                                Indent = true,
                                NewLineChars = "\r\n"
                            });
                        
                        // Save the XML to the stream
                        xmlDocument.Save(xmlWriter);

                        // Get the hash of the namespace and class for the instance of the _XML resource
                        var instance = System.Security.Cryptography.FNV64.GetHash(type.FullName);

                        // Set the name of the tuning _XML resource
                        if (nameMapResource.ContainsKey(instance))
                        {
                            nameMapResource.Remove(instance);
                        }
                        nameMapResource.Add(instance, type.FullName);

                        // Check if any _XML resource with that instance already exists and replace it with the XML stream if so, otherwise add a new one with said XML stream
                        var matchingTuningResourceIndexEntries = package.FindAll(x => x.ResourceType == 0x333406C && x.ResourceGroup == 0 && x.Instance == instance);
                        if (matchingTuningResourceIndexEntries.Count > 0)
                        {
                            package.DeleteResource(matchingTuningResourceIndexEntries[0]);
                        }
                        package.AddResource(new ResourceKey(0x333406C, 0, instance), xmlStream, true);
                    }
                }
            }
            // Check if any _KEY resource exists and replace it with the above modified name map if so, otherwise add a new one with the above name map
            if (nameMapResourceIndexEntries.Count > 0)
            {
                package.DeleteResource(nameMapResourceIndexEntries[0]);
            }
            package.AddResource(new ResourceKey(0x166038C, nameMapResourceIndexEntries.Count == 0 ? 0 : nameMapResourceIndexEntries[0].ResourceGroup, nameMapResourceIndexEntries.Count == 0 ? 0 : nameMapResourceIndexEntries[0].Instance), nameMapResource.Stream, true);

            // Save the modified package
            package.SavePackage();
        }
    }
}
