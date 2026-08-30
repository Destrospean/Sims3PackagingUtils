using System;
using System.Collections.Generic;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Destrospean.TuningResourceGenerator
{
    static class Program
    {
        static List<AssemblyDefinition> sAssemblies;

        public static List<AssemblyDefinition> Assemblies
        {
            get
            {
                if (sAssemblies == null)
                {
                    sAssemblies = new List<AssemblyDefinition>();
                    foreach (var name in typeof(Program).Assembly.GetManifestResourceNames())
                    {
                        if (name.EndsWith(".dll"))
                        {
                            sAssemblies.Add(AssemblyDefinition.ReadAssembly(typeof(Program).Assembly.GetManifestResourceStream(name)));
                        }
                    }
                }
                return sAssemblies;
            }
        }

        /// <summary>
        /// Groups the instructions within a constructor by their corresponding field names within the constructor's declaring class
        /// </summary>
        public static Dictionary<string, List<Instruction>> GetInstructionsByFieldName(MethodDefinition constructor, Predicate<Instruction> instructionPredicate = null)
        {
            var instructions = new List<Instruction>();
            var instructionsByFieldName = new Dictionary<string, List<Instruction>>();
            foreach (var instruction in constructor.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld)
                {
                    instructionsByFieldName[((FieldReference)instruction.Operand).Name] = instructions.FindAll(instructionPredicate ?? (x => true));
                    instructions.Clear();
                    continue;
                }
                instructions.Add(instruction);
            }
            return instructionsByFieldName;
        }

        /// <summary>
        /// Fetches the comments for all tunable fields (as strings; null for each tunable field that does not have a comment)
        /// </summary>
        public static string[] GetTunableComments(FieldDefinition[] tunableFields)
        {
            return Array.ConvertAll(tunableFields, x =>
                {
                    var index = Array.FindIndex(x.CustomAttributes.ToArray(), y => y.AttributeType.Name == "TunableComment" || y.AttributeType.Name == "TunableCommentAttribute");
                    if (index == -1)
                    {
                        return null;
                    }
                    var value = x.CustomAttributes[index].ConstructorArguments[0].Value.ToString();
                    value = value.Substring(value.StartsWith(" ") ? 1 : 0);
                    return " " + (value.EndsWith(" ") ? value.Remove(value.Length - 1) : value) + " ";
                });
        }

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

                    // Create the XmlDocument object and load the template for tuning XMLs
                    var xmlDocument = new XmlDocument
                        {
                            PreserveWhitespace = !Array.TrueForAll(GetTunableComments(tunableFields), x => x == null)
                        };
                    xmlDocument.LoadXml("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<base>\r\n  <Current_Tuning></Current_Tuning>\r\n</base>");

                    // Get the index of the static constructor if it exists
                    var staticConstructorIndex = Array.FindIndex(type.Methods.ToArray(), y => y.IsConstructor && y.IsStatic);

                    // Populate the node with the fields of the current type
                    xmlDocument.PopulateFields(xmlDocument.SelectSingleNode("base/Current_Tuning"), tunableFields, staticConstructorIndex == -1 ? new Dictionary<string, List<Instruction>>() : GetInstructionsByFieldName(type.Methods[staticConstructorIndex]));

                    if (tunableFields.Length > 0)
                    {
                        // Create the XML stream and writer
                        var xmlStream = new System.IO.MemoryStream();
                        var xmlWriter = XmlWriter.Create(xmlStream, new XmlWriterSettings
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

        public static void PopulateFields(this XmlDocument xmlDocument, XmlNode currentNode, FieldDefinition[] tunableFields, Dictionary<string, List<Instruction>> instructionsByFieldName, string indentation = "    ")
        {
            var tunableComments = GetTunableComments(tunableFields);

            // Iterate through each of the tunable fields of the current class
            for (var i = 0; i < tunableFields.Length; i++)
            {
                var field = tunableFields[i];

                // Check if there is a tunable comment for this field and add it to the XML if so
                if (tunableComments[i] != null)
                {
                    var tunableComment = xmlDocument.CreateComment(tunableComments[i]);
                    currentNode.AppendChild(tunableComment);
                    currentNode.InsertBefore(xmlDocument.CreateSignificantWhitespace("\r\n" + indentation), tunableComment);
                }

                object initialValue = null;
                XmlElement tunableElement = null;

                // Fetch any values assigned in the code to the tunable field
                List<Instruction> instructions;
                if (instructionsByFieldName.TryGetValue(field.Name, out instructions) && instructions.Count > 0)
                {
                    // Fetch the primitive value of the field
                    if (instructions.Count == 1)
                    {
                        if (instructions[0].OpCode == OpCodes.Newobj)
                        {
                            MethodDefinition methodDefinition;
                            if (TryGetMethodDefinition((MethodReference)instructions[0].Operand, out methodDefinition))
                            {
                                // Create and add the tunable field as an element in the XML
                                tunableElement = xmlDocument.CreateElement(field.Name);
                                currentNode.AppendChild(tunableElement);

                                // Populate the created node with the fields of said node's corresponding class
                                xmlDocument.PopulateFields(tunableElement, methodDefinition.DeclaringType.Fields.ToArray(), GetInstructionsByFieldName(methodDefinition, x => x.OpCode != OpCodes.Ldarg_0), indentation + "  ");
                            }
                        }

                        initialValue = instructions[0].Operand ?? (field.FieldType.Name == "Boolean" ? (object)(instructions[0].OpCode == OpCodes.Ldc_I4_1) : null);
                        if (initialValue == null)
                        {
                            switch (instructions[0].OpCode.Code)
                            {
                                case Code.Ldc_I4_M1:
                                    initialValue = -1;
                                    break;
                                case Code.Ldc_I4_0:
                                    initialValue = 0;
                                    break;
                                case Code.Ldc_I4_1:
                                    initialValue = 1;
                                    break;
                                case Code.Ldc_I4_2:
                                    initialValue = 2;
                                    break;
                                case Code.Ldc_I4_3:
                                    initialValue = 3;
                                    break;
                                case Code.Ldc_I4_4:
                                    initialValue = 4;
                                    break;
                                case Code.Ldc_I4_5:
                                    initialValue = 5;
                                    break;
                                case Code.Ldc_I4_6:
                                    initialValue = 6;
                                    break;
                                case Code.Ldc_I4_7:
                                    initialValue = 7;
                                    break;
                                case Code.Ldc_I4_8:
                                    initialValue = 8;
                                    break;
                            }
                        }
                    }
                    else if (instructions[instructions.Count - 1].OpCode == OpCodes.Newobj)
                    {
                        MethodDefinition methodDefinition;
                        if (TryGetMethodDefinition((MethodReference)instructions[instructions.Count - 1].Operand, out methodDefinition))
                        {
                            // Fetch all the tunable fields of the declaring type
                            var tempTunableFields = methodDefinition.DeclaringType.Fields.ToArray();

                            var tempInstructionsByFieldName = new Dictionary<string, List<Instruction>>();
                            for (var j = 0; j < instructions.Count - 1; j++)
                            {
                                tempInstructionsByFieldName[tempTunableFields[j].Name] = new List<Instruction>
                                    {
                                        instructions[j]
                                    };
                            }

                            // Create and add the tunable field as an element in the XML
                            tunableElement = xmlDocument.CreateElement(field.Name);
                            currentNode.AppendChild(tunableElement);

                            // Populate the created node with the fields of said node's corresponding class
                            xmlDocument.PopulateFields(tunableElement, tempTunableFields, tempInstructionsByFieldName, indentation + "  ");
                        }
                    }
                    // Fetch the array value of the field
                    else if (instructions[1].OpCode == OpCodes.Newarr)
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
                                arrayString += instructions[j].Operand + ",";
                            }
                        }
                        initialValue = arrayString.EndsWith(",") ? arrayString.Remove(arrayString.Length - 1) : arrayString;
                    }
                }

                // Create and add the tunable field as an element in the XML
                if (tunableElement == null)
                {
                    tunableElement = xmlDocument.CreateElement(field.Name);
                    tunableElement.SetAttribute("value", (initialValue is float ? ((float)initialValue).ToString(System.Globalization.CultureInfo.InvariantCulture) : initialValue)?.ToString() ?? (field.FieldType.Name == "Boolean" ? "False" : field.FieldType.Name == "String" || field.FieldType.IsArray ? "" : "0"));
                    currentNode.AppendChild(tunableElement);
                }

                // Do formatting for XMLs with comments
                if (!Array.TrueForAll(tunableComments, x => x == null))
                {
                    currentNode.InsertBefore(xmlDocument.CreateSignificantWhitespace("\r\n" + indentation), tunableElement);
                    currentNode.InsertAfter(xmlDocument.CreateSignificantWhitespace(i == tunableFields.Length - 1 ? "\r\n" + indentation.Substring(2) : "\r\n"), tunableElement);
                }
            }
        }

        public static bool TryGetMethodDefinition(MethodReference methodReference, out MethodDefinition methodDefinition)
        {
            methodDefinition = null;
            try
            {
                methodDefinition = methodReference.Resolve();
                return true;
            }
            catch (AssemblyResolutionException)
            {
                foreach (var assembly in Assemblies)
                {
                    foreach (var type in assembly.MainModule.GetTypes())
                    {
                        if (type.FullName == methodReference.DeclaringType.FullName)
                        {
                            foreach (var method in type.Methods)
                            {
                                if (method.FullName == methodReference.FullName)
                                {
                                    methodDefinition = method;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }
    }
}
