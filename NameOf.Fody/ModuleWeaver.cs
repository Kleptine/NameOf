﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NameOf.Fody {
    public static class InstructionExtensions {
        public static IEnumerable<Instruction> AsReverseEnumerable(this Instruction instruction) {
            yield return instruction;
            while (instruction.Previous != null)
                yield return instruction = instruction.Previous;
        }
    }
    public partial class ModuleWeaver {
        public ModuleDefinition ModuleDefinition { get; set; }
        //private static readonly List<TypeDefinition> PotentiallyUnusedTypeDefinitions = new List<TypeDefinition>();
        private static readonly List<MethodDefinition> PotentiallyUnusedMethodDefinitions = new List<MethodDefinition>();
        private static readonly List<FieldDefinition> PotentiallyUnusedFieldDefinitions = new List<FieldDefinition>();
        //private static void AddToPotentiallyUnusedTypes(Instruction instruction) {
        //    PotentiallyUnusedTypeDefinitions.Add(instruction.Operand as TypeDefinition);
        //}
        public void Execute() {
            var allMethods = ModuleDefinition.GetTypes().SelectMany(x => x.Methods.Where(y => y.HasBody));
            foreach (var method in allMethods)
                ProcessMethod(method);
            // Check for remaining calls to `Name.Of`, which are invalid at this point.
            foreach (var method in allMethods) {
                Instruction nameOfCallInstruction = method.Body.Instructions.FirstOrDefault(IsNameOfCallInstruction);
                if (nameOfCallInstruction != null)
                    throw GetNotSupportedException(nameOfCallInstruction); // The usage of Name.Of is not supported
            }
            // Remove Name.Of reference
            var nameOfAssemblyReference = ModuleDefinition.AssemblyReferences.SingleOrDefault(x => x.FullName == typeof(Name).Assembly.FullName);
            ModuleDefinition.AssemblyReferences.Remove(nameOfAssemblyReference);
            // TODO: Make sure Name.Of.dll isn't placed in the build directory
            // TODO: Remove unused lambdas generated by some uses of `Name.Of`
            foreach (var unusedMethodDefinition in PotentiallyUnusedMethodDefinitions.Where(x => !IsUsed(x)))
                unusedMethodDefinition.DeclaringType.Methods.Remove(unusedMethodDefinition);
            foreach (var unusedFieldDefinition in PotentiallyUnusedFieldDefinitions.Where(x => !IsUsed(x)))
                unusedFieldDefinition.DeclaringType.Fields.Remove(unusedFieldDefinition);
        }
        Boolean IsUsed(MethodDefinition methodDefinition) {
            return ModuleDefinition.GetTypes().SelectMany(x => x.Methods).SelectMany(x => x.Body.Instructions).Any(x => CallOpCodes.Contains(x.OpCode) && x.Operand == methodDefinition);
        }
        Boolean IsUsed(FieldDefinition fieldDefinition) {
            return ModuleDefinition.GetTypes().SelectMany(x => x.Methods).SelectMany(x => x.Body.Instructions).Any(x => LoadFieldOpCodes.Contains(x.OpCode) && x.Operand == fieldDefinition);
        }
        private static readonly MethodInfo NameOfMethodInfo = new Func<Object, String>(Name.Of).Method;
        private static readonly String NameOfMethodSignature = String.Format("{0} {1}::{2}", NameOfMethodInfo.ReturnType, NameOfMethodInfo.DeclaringType, NameOfMethodInfo.Name);
        private static Boolean IsNameOfCallInstruction(Instruction instruction) {
            return instruction.OpCode == OpCodes.Call && ((MethodReference) instruction.Operand).FullName.StartsWith(NameOfMethodSignature);
        }
        private static void ProcessMethod(MethodDefinition methodDefinition) {
            var nameOfCallInstructions = methodDefinition.Body.Instructions.Where(IsNameOfCallInstruction).ToList();
            var methodBodyIlProcessor = methodDefinition.Body.GetILProcessor();
            foreach (var nameOfCallInstruction in nameOfCallInstructions)
                ProcessNameOfCallInstruction(nameOfCallInstruction, methodBodyIlProcessor);
        }
        private static Int32 GetNumberAtEndOf(OpCode opCode) {
            Int32 number = Int32.Parse(opCode.ToString().Last().ToString());
            if (opCode.ToString().Reverse().Skip(1).First() == 'm')
                return number * -1;
            return number;
        }
        private static readonly OpCode[] CallOpCodes = { OpCodes.Call, OpCodes.Calli, OpCodes.Callvirt };
        private static readonly OpCode[] LoadFieldOpCodes = { OpCodes.Ldfld, OpCodes.Ldsfld, OpCodes.Ldflda, OpCodes.Ldsflda };
        private static readonly OpCode[] LoadOpCodes = LoadFieldOpCodes.Concat(new[] { OpCodes.Ldloc, OpCodes.Ldloc_S, OpCodes.Ldloca, OpCodes.Ldloca_S, OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 }).ToArray();
        private static readonly MethodInfo GetTypeFromHandleMethodInfo = new Func<RuntimeTypeHandle, Object>(Type.GetTypeFromHandle).Method;
        private static readonly String GetTypeFromHandleMethodSignature = String.Format("{0} {1}::{2}(", GetTypeFromHandleMethodInfo.ReturnType, GetTypeFromHandleMethodInfo.DeclaringType, GetTypeFromHandleMethodInfo.Name);

        private static String GetNameFromAnonymousMethod(Instruction anonymousMethodCallInstruction, ILProcessor ilProcessor) {
            var instruction = ((MethodDefinition)anonymousMethodCallInstruction.Operand).Body.Instructions.Last();
            Boolean patternNotMatched = true;
            PatternInstruction[] patternMatched = null;
            Terminal terminal = null;
            Instruction terminalInstruction = null;
            var patternsOrdered = lambdaPatterns.OrderByDescending(x => x.Count()); // Ordered by length of pattern to ensure longest possible patterns (of which shorter patterns might be a subset) are checked first
            foreach (var pattern in patternsOrdered) {
                Instruction iterator = instruction;
                patternNotMatched = false;
                patternMatched = null;
                terminal = null;
                foreach (var patternInstruction in pattern.Reverse()) {
                    if (patternInstruction is OptionalPatternInstruction && !patternInstruction.EligibleOpCodes.Contains(iterator.OpCode))
                        continue;
                    if (!patternInstruction.EligibleOpCodes.Contains(iterator.OpCode) || !patternInstruction.IsPredicated(iterator, ilProcessor)) {
                        patternNotMatched = true;
                        break;
                    }
                    if (patternInstruction.Terminal != null) {
                        terminalInstruction = iterator;
                        terminal = patternInstruction.Terminal;
                    }
                    iterator = iterator.Previous;
                }
                if (!patternNotMatched) {
                    patternMatched = pattern;
                    break;
                }
            }
            if (patternNotMatched)
                throw GetInvalidTerminalYieldException();
            if (terminal == null)
                throw new NotImplementedException("There is no terminal expression implemented for the matched pattern.");
            String name = terminal(terminalInstruction, ilProcessor);
            if (name == null) {
                throw GetInvalidTerminalYieldException();
            }
            PotentiallyUnusedMethodDefinitions.Add(anonymousMethodCallInstruction.Operand as MethodDefinition);
            return name;
        }

		private static String GetNameFromAsyncAwaitLocal(Instruction i, ILProcessor p) {
			var operand = ((FieldReference)i.Operand).Name;
			var matchGroup = Regex.Match(operand, "<(?<name>[^>]+)>").Groups["name"];
			if (matchGroup.Success)
				return matchGroup.Value;
			return operand;
		}

		//private static IEnumerable<PatternInstruction[]> PatternsReversedAndOrderedByPatternLength = from nameOfCallPattern in nameOfCallPatterns
		//																							 orderby nameOfCallPattern.Length descending
		//																							 select nameOfCallPattern.Reverse().ToArray();
        private static void ProcessNameOfCallInstruction(Instruction instruction, ILProcessor ilProcessor) {
            var patterns = from nameOfCallPattern in nameOfCallPatterns
                           orderby nameOfCallPattern.Length descending
                           select nameOfCallPattern.Reverse().ToArray();
            var instructions = instruction.AsReverseEnumerable().Take(patterns.First().Length).ToArray(); // Take an instruction set with a maximum size of the longest pattern.
            Boolean possibleMatch = false;
            PatternInstruction[] patternMatched = null;
            Func<String> terminal = null;
            foreach (var pattern in patterns) {
                possibleMatch = true;
                terminal = null;
                for (Int32 i = 0, j = 0; i < pattern.Length && j < instructions.Length; ++i, ++j) {
                    while (pattern[i] is OptionalPatternInstruction && !pattern[i].EligibleOpCodes.Contains(instructions[j].OpCode))
                        ++i;
                    var patternInstruction = pattern[i];
                    var currentInstruction = instructions[j];
                    if (patternInstruction.EligibleOpCodes.Contains(currentInstruction.OpCode) && patternInstruction.IsPredicated(currentInstruction, ilProcessor)) {
                        if (patternInstruction.Terminal != null && terminal == null)
                            terminal = () => patternInstruction.Terminal(currentInstruction, ilProcessor);
                        if (patternInstruction.Action != null)
                            patternInstruction.Action(currentInstruction);
                    }
                    else {
                        possibleMatch = false;
                        break;
                    }
                }
                if (possibleMatch && pattern.Count(x => !(x is OptionalPatternInstruction)) <= instructions.Length) {
                    patternMatched = pattern;
                    break;
                }
            }
            if (!possibleMatch)
                throw GetNotSupportedException(instruction); // The usage of Name.Of is not supported
            if (terminal == null)
                throw new NotImplementedException("There is no terminal expression implemented for the matched pattern.");
            String name;
            try {
                name = terminal();
                if (String.IsNullOrWhiteSpace(name))
                    throw new Exception("Name not found.");
            }
            catch {
                throw GetNotSupportedException(instruction);
            }
            // TODO: Remove the anonymous methods generated by lamdba expressions in some uses of Name.Of...
            ilProcessor.InsertAfter(instruction, Instruction.Create(OpCodes.Ldstr, name));
            for (Int32 i = 0, j = 0; i < patternMatched.Length && j < instructions.Length; ++i, ++j) {
                while (patternMatched[i] is OptionalPatternInstruction && !patternMatched[i].EligibleOpCodes.Contains(instructions[j].OpCode))
                    ++i;
                ilProcessor.Remove(instructions[j]);
            }
        }
        private static InvalidOperationException GetInvalidTerminalYieldException() {
            return new InvalidOperationException("Terminal didn't yield a valid name.");
        }
        private static NotSupportedException GetNotSupportedException(Instruction instruction) {
            String exceptionMessage = String.Format("This usage of '{0}.{1}' is not supported. {2}",
                                                    NameOfMethodInfo.DeclaringType.Name,
                                                    NameOfMethodInfo.Name,
                                                    GetSequencePointText(instruction));
            return new NotSupportedException(exceptionMessage);
        }
        private static String GetSequencePointText(Instruction instruction) {
            var i = instruction;
            while (i.SequencePoint == null && i.Previous != null) // Look for last sequence point
                i = i.Previous;
            if (i.SequencePoint == null)
                return "No source line information available.";
            return String.Format("Source: {0} - line {1}", i.SequencePoint.Document.Url, i.SequencePoint.StartLine);
        }
    }
}