﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NameOf.Fody {
	public partial class ModuleWeaver {
		public ModuleDefinition ModuleDefinition { get; set; }
		private static readonly List<MethodDefinition> PotentiallyUnusedMethodDefinitions = new List<MethodDefinition>();
		private static readonly List<FieldDefinition> PotentiallyUnusedFieldDefinitions = new List<FieldDefinition>();

		public void Execute() {
			var allMethods = ModuleDefinition.GetTypes().SelectMany(x => x.Methods.Where(y => y.HasBody)).ToArray();
			foreach (var method in allMethods)
				ProcessMethod(method);

			// Check for remaining calls to `Name.Of`, which are invalid at this point.
			foreach (var method in allMethods) {
				var nameOfCallInstruction = method.Body.Instructions.FirstOrDefault(IsNameOfCallInstruction);
				if (nameOfCallInstruction != null)
					throw GetNotSupportedException(nameOfCallInstruction); // The usage of Name.Of is not supported
			}

			// Remove Name.Of reference
			var nameOfAssemblyReference = ModuleDefinition.AssemblyReferences.Single(x => x.Name == "Name.Of");
			ModuleDefinition.AssemblyReferences.Remove(nameOfAssemblyReference);

			foreach (var unusedMethodDefinition in PotentiallyUnusedMethodDefinitions.Where(IsNotUsed))
				if (unusedMethodDefinition.DeclaringType != null)
					if (unusedMethodDefinition.DeclaringType.Methods != null)
						unusedMethodDefinition.DeclaringType.Methods.Remove(unusedMethodDefinition);

			foreach (var unusedFieldDefinition in PotentiallyUnusedFieldDefinitions.Where(IsNotUsed))
				if (unusedFieldDefinition.DeclaringType != null)
					if (unusedFieldDefinition.DeclaringType.Fields != null)
						unusedFieldDefinition.DeclaringType.Fields.Remove(unusedFieldDefinition);
		}

		private Boolean IsNotUsed(IMetadataTokenProvider methodDefinition) {
			return !ModuleDefinition.GetTypes()
				                    .SelectMany(x => x.Methods)
				                    .SelectMany(x => x.Body.Instructions)
				                    .Any(x => CallOpCodes.Contains(x.OpCode) && x.Operand == methodDefinition);
		}

		private Boolean IsNotUsed(FieldDefinition fieldDefinition) {
			return !ModuleDefinition.GetTypes()
				                    .SelectMany(x => x.Methods)
				                    .SelectMany(x => x.Body.Instructions)
				                    .Any(x => LoadFieldOpCodes.Contains(x.OpCode) && x.Operand == fieldDefinition);
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

		private static readonly MethodInfo GetTypeFromHandleMethodInfo = new Func<RuntimeTypeHandle, Object>(Type.GetTypeFromHandle).Method;

		private static readonly String GetTypeFromHandleMethodSignature = String.Format("{0} {1}::{2}(",
			GetTypeFromHandleMethodInfo.ReturnType,
			GetTypeFromHandleMethodInfo.DeclaringType,
			GetTypeFromHandleMethodInfo.Name);

		private static String GetNameFromAnonymousMethod(Instruction anonymousMethodCallInstruction, ILProcessor ilProcessor) {
			var instruction = ((MethodDefinition) anonymousMethodCallInstruction.Operand).Body.Instructions.Last();
			Boolean patternIsNotMatched = true;
			Terminal terminal = null;
			Instruction terminalInstruction = null;
			var patternsOrdered = lambdaPatterns.OrderByDescending(x => x.Count()); // Ordered by length of pattern to ensure longest possible patterns (of which shorter patterns might be a subset) are checked first
			foreach (var pattern in patternsOrdered) {
				Instruction iterator = instruction;
				patternIsNotMatched = false;
				terminal = null;
				foreach (var patternInstruction in pattern.Reverse()) {
					if (patternInstruction is OptionalPatternInstruction && !patternInstruction.EligibleOpCodes.Contains(iterator.OpCode))
						continue;
					if (!patternInstruction.EligibleOpCodes.Contains(iterator.OpCode) || !patternInstruction.IsPredicated(iterator, ilProcessor)) {
						patternIsNotMatched = true;
						break;
					}
					if (patternInstruction.Terminal != null) {
						terminalInstruction = iterator;
						terminal = patternInstruction.Terminal;
					}
					iterator = iterator.Previous;
				}
				if (!patternIsNotMatched)
					break;
			}
			if (patternIsNotMatched)
				throw GetInvalidTerminalYieldException();
			if (terminal == null)
				throw new NotImplementedException("There is no terminal expression implemented for the matched pattern.");
			String name = terminal(terminalInstruction, ilProcessor);
			if (name == null)
				throw GetInvalidTerminalYieldException();
			if (anonymousMethodCallInstruction.Operand != null)
				PotentiallyUnusedMethodDefinitions.Add((MethodDefinition) anonymousMethodCallInstruction.Operand);
			return name;
		}

		private static String GetNameFromAsyncAwaitLocal(Instruction i, ILProcessor p) {
			var operand = ((FieldReference) i.Operand).Name;
			var matchGroup = Regex.Match(operand, "<(?<name>[^>]+)>").Groups["name"];
			if (matchGroup.Success)
				return matchGroup.Value;
			return operand;
		}

		private static readonly Lazy<IEnumerable<PatternInstruction[]>> patterns = new Lazy<IEnumerable<PatternInstruction[]>>(() => from nameOfCallPattern in nameOfCallPatterns
		                                                                                                                             orderby nameOfCallPattern.Length descending
		                                                                                                                             select nameOfCallPattern.Reverse().ToArray());
		private static void ProcessNameOfCallInstruction(Instruction instruction, ILProcessor ilProcessor) {
			var instructions = instruction.AsReverseEnumerable().Take(patterns.Value.First().Length).ToArray(); // Take an instruction set with a maximum size of the longest pattern.
			Boolean possibleMatch = false;
			PatternInstruction[] patternMatched = null;
			Func<String> terminal = null;
			foreach (var pattern in patterns.Value) {
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