using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

public class ReturnFixer
{
    public MethodDefinition Method;
    Instruction nopForHandleEnd;
    Collection<Instruction> instructions;
    public Instruction NopBeforeReturn;
    public Instruction NopJustBeforeReturn;
    Instruction sealBranchesNop;
	public VariableDefinition ReturnVariable;

    public void MakeLastStatementReturn()
    {

        instructions = Method.Body.Instructions;
        FixHangingHandlerEnd();

        sealBranchesNop = Instruction.Create(OpCodes.Nop);
        instructions.Add(sealBranchesNop);

        NopBeforeReturn = Instruction.Create(OpCodes.Nop);
	    NopJustBeforeReturn = Instruction.Create(OpCodes.Nop);

        if (IsMethodReturnValue())
        {
	        ReturnVariable = Method.Body.DeclareVariable("$returnVariable", Method.MethodReturnType.ReturnType);
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            var operand = instructions[index].Operand as Instruction;

	        if (operand == null || operand.OpCode != OpCodes.Ret)
	        {
		        continue;
	        }

	        if (operand.OpCode == OpCodes.Ret && instructions[index - 1].OpCode == OpCodes.Throw)
	        {
		        instructions.Insert(index, Instruction.Create(OpCodes.Br, NopJustBeforeReturn));
		        index ++;
		        instructions[index].Operand = sealBranchesNop;
		        continue;
	        }

			if (IsMethodReturnValue())
	        {
		        // The C# compiler never jumps directly to a ret
		        // when returning a value from the method. But other Fody
		        // modules and other compilers might. So store the value here.
		        instructions.Insert(index, Instruction.Create(OpCodes.Stloc, ReturnVariable));
		        instructions.Insert(index, Instruction.Create(OpCodes.Dup));
		        index += 2;
	        }

	        instructions[index].Operand = sealBranchesNop;
        }

	    if (IsMethodReturnValue())
	    {
		    WithReturnValue();
	    }
	    else
	    {
		    WithNoReturn();
	    }
    }

    bool IsMethodReturnValue()
    {
        return Method.MethodReturnType.ReturnType.Name != "Void";
    }

    void FixHangingHandlerEnd()
    {
        if (Method.Body.ExceptionHandlers.Count == 0)
        {
            return;
        }

        nopForHandleEnd = Instruction.Create(OpCodes.Nop);
        Method.Body.Instructions.Add(nopForHandleEnd);
        foreach (var handler in Method.Body.ExceptionHandlers)
        {
            if (handler.HandlerStart != null && handler.HandlerEnd == null)
            {
                handler.HandlerEnd = nopForHandleEnd;
            }
        }
    }


    void WithReturnValue()
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode == OpCodes.Ret)
            {
				if (instructions[index - 1].OpCode != OpCodes.Throw)
	            {
		            instructions.Insert(index, Instruction.Create(OpCodes.Stloc, ReturnVariable));
		            index++;
	            }

				instruction.OpCode = OpCodes.Br;
                instruction.Operand = sealBranchesNop;
                index++;
            }
        }
        instructions.Add(NopBeforeReturn);
        instructions.Add(Instruction.Create(OpCodes.Ldloc, ReturnVariable));
        instructions.Add(NopJustBeforeReturn);
        instructions.Add(Instruction.Create(OpCodes.Ret));

    }

    void WithNoReturn()
    {

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCodes.Ret)
            {
                instruction.OpCode = OpCodes.Br;
                instruction.Operand = sealBranchesNop;
            }
        }
        instructions.Add(NopBeforeReturn);
        instructions.Add(NopJustBeforeReturn);
        instructions.Add(Instruction.Create(OpCodes.Ret));
    }

}