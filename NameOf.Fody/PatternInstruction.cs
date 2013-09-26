﻿using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Mono.Cecil.Cil;

namespace NameOf.Fody {
    delegate String Terminal(Instruction instruction, ILProcessor ilProcessor);
    delegate Boolean Predicate(Instruction instruction, ILProcessor ilProcessor);
    class PatternInstruction {
        public OpCode[] EligibleOpCodes { get; private set; }
        public Terminal Terminal { get; private set; }
        private readonly Predicate predicate;
        public Boolean IsPredicated(Instruction instruction, ILProcessor ilProcessor) {
            try {
                return predicate(instruction, ilProcessor);
            }
            catch (Exception) {
                return false;
            }
        }
        private static String NameOf<T>(Expression<Func<T>> expression) {
            if (expression == null)
                throw new ArgumentNullException(NameOf(() => expression));
            var body = expression.Body as MemberExpression;
            if (body == null)
                throw new ArgumentException(NameOf(() => expression));
            return body.Member.Name;
        }
        private static Boolean PredicateDummy(Instruction instruction, ILProcessor ilProcessor) { return true; }
        public PatternInstruction(OpCode[] eligibleOpCodes, Terminal terminal = null, Predicate predicate = null) {
            if (eligibleOpCodes == null)
                throw new ArgumentNullException(NameOf(() => eligibleOpCodes)); // I know... this is ironic. It would be ideal to have the project use its own Name.Of, but I haven't put in the time to make sure that works.
            if (eligibleOpCodes.Length == 0)
                throw new ArgumentException(NameOf(() => eligibleOpCodes));
            EligibleOpCodes = eligibleOpCodes;
            Terminal = terminal;
            this.predicate = predicate ?? PredicateDummy;
        }
        public PatternInstruction(OpCode opCode, Terminal terminal = null, Predicate predicate = null) : this(new[] { opCode }, terminal, predicate) { }
    }
    class OptionalPatternInstruction : PatternInstruction {
        public OptionalPatternInstruction(OpCode[] eligibleOpCodes, Terminal terminal = null, Predicate predicate = null) : base(eligibleOpCodes, terminal, predicate) { }
        public OptionalPatternInstruction(OpCode opCode, Terminal terminal = null, Predicate predicate = null) : base(opCode, terminal, predicate) { }
    }
    class NameOfPatternInstruction : PatternInstruction {
        public NameOfPatternInstruction(Terminal terminal = null, Predicate predicate = null) : base(OpCodes.Call, terminal, predicate) { }
    }
}
;