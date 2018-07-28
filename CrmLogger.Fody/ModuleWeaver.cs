#region Imports

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MethodBody = Mono.Cecil.Cil.MethodBody;

#endregion

public class ModuleWeaver
{
	public ModuleDefinition ModuleDefinition { get; set; }

	public MethodDefinition Method;
	private MethodBody body;

	private VariableDefinition exceptionVariable;

	//public TypeReference ExceptionType;

	public FieldReference LoggerField;
	public bool IsInstanceLogger;

	public MethodReference StartMethod;
	public MethodReference ParamsMethod;
	public MethodReference ExceptionMethod;
	public MethodReference EndMethod;

	public TypeReference String => ModuleDefinition.TypeSystem.String;
	public TypeReference Object => ModuleDefinition.TypeSystem.Object;

	public AssemblyDefinition Mscorlib { get; set; }
	public TypeReference ExceptionType
	{
		get; set;
	}

	public IAssemblyResolver AssemblyResolver { get; set; }
	public TypeDefinition LoggerTypeDefinition { get; set; }

	#region Unused

	//// Will contain the full element XML from FodyWeavers.xml. OPTIONAL
	//public XElement Config
	//{
	//	get; set;
	//}

	// Will log an MessageImportance.Normal message to MSBuild. OPTIONAL
	public Action<string> LogDebug
	{
		get; set;
	}

	// Will log an MessageImportance.High message to MSBuild. OPTIONAL
	public Action<string> LogInfo
	{
		get; set;
	}

	//// Will log a message to MSBuild. OPTIONAL
	//public Action<string, MessageImportance> LogMessage
	//{
	//	get; set;
	//}

	// Will log an warning message to MSBuild. OPTIONAL
	public Action<string> LogWarning
	{
		get; set;
	}

	//// Will log an warning message to MSBuild at a specific point in the code. OPTIONAL
	//public Action<string, SequencePoint> LogWarningPoint
	//{
	//	get; set;
	//}

	// Will log an error message to MSBuild. OPTIONAL
	public Action<string> LogError
	{
		get; set;
	}

	//// Will log an error message to MSBuild at a specific point in the code. OPTIONAL
	//public Action<string, SequencePoint> LogErrorPoint
	//{
	//	get; set;
	//}

	// Will contain the full path of the target assembly. OPTIONAL
	public string AssemblyFilePath
	{
		get; set;
	}

	//// Will contain the full directory path of the target project. 
	//// A copy of $(ProjectDir). OPTIONAL
	//public string ProjectDirectoryPath
	//{
	//	get; set;
	//}

	//// Will contain the full directory path of the current weaver. OPTIONAL
	//public string AddinDirectoryPath
	//{
	//	get; set;
	//}

	//// Will contain the full directory path of the current solution.
	//// A copy of `$(SolutionDir)` or, if it does not exist, a copy of `$(MSBuildProjectDirectory)..\..\..\`. OPTIONAL
	//public string SolutionDirectoryPath
	//{
	//	get; set;
	//}

	#endregion

	public void Execute()
	{
		if (AssemblyFilePath.Contains("\\obj\\Debug"))
		{
			LogWarning("~~~~~ Skipping weaving because of a debug build ~~~~~");
			return;
		}

		LoadSystemTypes();
		Init();

		foreach (var type in ModuleDefinition.GetTypes()
			.Where(x => (x.BaseType != null) && !x.IsEnum && !x.IsInterface))
		{
			ProcessType(type);
		}
	}

	public void LoadSystemTypes()
	{
		Mscorlib = AssemblyResolver.Resolve(new AssemblyNameReference("mscorlib", null));

		var exceptionType = Mscorlib?.MainModule.Types.FirstOrDefault(x => x.Name == "Exception");

		if (exceptionType == null)
		{
			var runtime = AssemblyResolver.Resolve(new AssemblyNameReference("System.Runtime", null));
			exceptionType = runtime.MainModule.Types.First(x => x.Name == "Exception");
		}

		ExceptionType = ModuleDefinition.ImportReference(exceptionType);
	}

	private string GetPackagesFolder(string currentFolder = null)
	{
		while (true)
		{
			currentFolder = currentFolder ?? Directory.GetCurrentDirectory();
			var packagesFolder = Path.Combine(currentFolder, "packages");
			var isPackagesFolderExists = Directory.Exists(packagesFolder);

			if (isPackagesFolderExists)
			{
				return packagesFolder;
			}

			currentFolder = Directory.GetParent(currentFolder).FullName;
		}
	}

	ModuleDefinition CommonModuleDefinition;

	public void Init()
	{
		LoggerTypeDefinition = ModuleDefinition.Types.FirstOrDefault(x => x.Name == "CrmLog");

		if (LoggerTypeDefinition == null)
		{
			// find the common assembly in the NuGet packages
			var packagesFolder = GetPackagesFolder();
			var assemblyPath = Directory
				.GetFiles(packagesFolder, "LinkDev.Libraries.Common.dll", SearchOption.AllDirectories).FirstOrDefault();

			LogInfo($"{assemblyPath}");

			if (assemblyPath != null)
			{
				CommonModuleDefinition = AssemblyDefinition.ReadAssembly(assemblyPath).MainModule;
				LoggerTypeDefinition = CommonModuleDefinition.Types.FirstOrDefault(x => x.Name.Contains("CrmLog"));
				LogInfo($"{LoggerTypeDefinition?.FullName}");
				ModuleDefinition.ImportReference(LoggerTypeDefinition);
			}
		}
		else
		{
			CommonModuleDefinition = ModuleDefinition;
		}

		if (LoggerTypeDefinition == null)
		{
			throw new WeavingException("Cannot find CrmLog type in this assembly."
				+ " Please make sure that Common.cs exists in the project or its assembly reference.");
		}

		StartMethod = CommonModuleDefinition.ImportReference(LoggerTypeDefinition
			.FindMethod("LogFunctionStart", "LogEntry", "IExecutionContext", "String", "String", "String", "Int32"));
		StartMethod = ModuleDefinition.ImportReference(StartMethod);

		ParamsMethod = CommonModuleDefinition.ImportReference(LoggerTypeDefinition
			.FindMethod("LogKeyValues", "String", "String[]", "Object[]", "LogLevel", "String", "String",
				"IExecutionContext", "String", "Int32"));
		ParamsMethod = ModuleDefinition.ImportReference(ParamsMethod);

		ExceptionMethod = CommonModuleDefinition.ImportReference(LoggerTypeDefinition
			.FindMethod("Log", "Exception", "String", "String", "String", "String", "String", "String", "Int32"));
		ExceptionMethod = ModuleDefinition.ImportReference(ExceptionMethod);

		EndMethod = CommonModuleDefinition.ImportReference(LoggerTypeDefinition
			.FindMethod("LogFunctionEnd", "LogEntry", "IExecutionContext", "String", "String", "String", "Int32"));
		EndMethod = ModuleDefinition.ImportReference(EndMethod);

		exceptionVariable = new VariableDefinition(ModuleDefinition.ImportReference(ExceptionType));
	}

	private void ProcessType(TypeDefinition type)
	{
		Method = null;
		var isTypeLogAttributeExist = type.CustomAttributes.ContainsAttribute("LinkDev.Libraries.Common.LogAttribute");
		var isTypeNoLogAttributeExist = type.CustomAttributes.ContainsAttribute("LinkDev.Libraries.Common.NoLogAttribute");

		if (isTypeNoLogAttributeExist)
		{
			return;
		}

		var isLoggedTypeInConsole = false;

		foreach (var method in type.Methods)
		{
			var isMethodLogAttributeExist = method.CustomAttributes.ContainsAttribute("LinkDev.Libraries.Common.LogAttribute");
			var isMethodNoLogAttributeExist = method.CustomAttributes.ContainsAttribute("LinkDev.Libraries.Common.NoLogAttribute");
			var isLogMethod = isMethodLogAttributeExist
				|| (isTypeLogAttributeExist && !isMethodNoLogAttributeExist);
			var isAsync = method.CustomAttributes.Any(a => a.AttributeType.Name == "AsyncStateMachineAttribute");
			var isStatic = method.IsStatic;

			//skip for abstract and delegates
			if (!isLogMethod || isAsync || !method.HasBody || method.IsConstructor || method.IsAnonymous())
			{
				continue;
			}

			LoggerField = type.GetLoggerWithProperType(LoggerTypeDefinition);

			if (LoggerField == null)
			{
				throw new WeavingException($"Cannot find logger field in class: {type.FullName}");
			}

			if (LoggerField.Resolve() == null)
			{
				throw new WeavingException($"Cannot form proper typed access for the logger field in class:"
					+ $" {LoggerField.DeclaringType.FullName}");
			}

			IsInstanceLogger = LoggerField.Resolve()?.IsStatic == false;
			
			if (IsInstanceLogger && isStatic)
			{
				throw new WeavingException($"Cannot find a static logger field in class: {type.FullName},"
					+ $" for static function: {method.Name}.");
			}

			Method = method;

			if (!isLoggedTypeInConsole)
			{
				LogInfo($">>>>> Processing: {type.FullName} <<<<<");
				LogInfo($"Weaving CRM Log pattern into the following methods ...");
				isLoggedTypeInConsole = true;
			}

			LogInfo($"> {method.Name}");
			Inject();
		}

		if (isLoggedTypeInConsole)
		{
			LogInfo($"^^^^^ Finished: {type.FullName} ^^^^^");
		}
	}

	private void Inject()
	{
		body = Method.Body;

		body.SimplifyMacros();

		var ilProcessor = body.GetILProcessor();

		var returnFixer = new ReturnFixer
						  {
							  Method = Method
						  };
		returnFixer.MakeLastStatementReturn();

		var methodBodyFirstInstruction = GetMethodBodyFirstInstruction();

		var startInstructions = new List<Instruction>();
		startInstructions.AddRange(GetStartInstructions());

		foreach (var instruction in startInstructions)
		{
			ilProcessor.InsertBefore(methodBodyFirstInstruction, instruction);
		}

		var paramInstructions = GetParamInstructions();
		paramInstructions.Reverse();

		if (paramInstructions.Any())
		{
			foreach (var instruction in paramInstructions)
			{
				ilProcessor.InsertAfter(startInstructions.Last(), instruction);
			}
		}

		ilProcessor.InsertBefore(returnFixer.NopBeforeReturn, GetReturnValueInstructions(returnFixer.ReturnVariable));

		var tryCatchLeaveInstructions = Instruction.Create(OpCodes.Leave, returnFixer.NopBeforeReturn);
		var catchInstructions = new List<Instruction>();

		catchInstructions.AddRange(GetCatchInstructions());
		ilProcessor.InsertBefore(returnFixer.NopBeforeReturn, tryCatchLeaveInstructions);
		ilProcessor.InsertBefore(returnFixer.NopBeforeReturn, catchInstructions);

		var endInstructions = new List<Instruction>();
		endInstructions.AddRange(GetEndInstructions());
		var finallyInstruction = Instruction.Create(OpCodes.Endfinally);
		endInstructions.Add(finallyInstruction);
		endInstructions.Reverse();

		foreach (var instruction in endInstructions)
		{
			ilProcessor.InsertAfter(catchInstructions.Last(), instruction);
		}

		var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
		{
			CatchType = ExceptionType,
			TryStart = methodBodyFirstInstruction,
			TryEnd = tryCatchLeaveInstructions.Next,
			HandlerStart = catchInstructions.First(),
			HandlerEnd = catchInstructions.Last().Next
		};

		body.ExceptionHandlers.Add(handler);

		handler = new ExceptionHandler(ExceptionHandlerType.Finally)
		{
			TryStart = methodBodyFirstInstruction,
			TryEnd = catchInstructions.Last().Next,
			HandlerStart = catchInstructions.Last().Next,
			HandlerEnd = finallyInstruction.Next
		};

		body.ExceptionHandlers.Add(handler);

		var instructions = body.Instructions;
		Instruction doubleDupInstruction = null;

		for (var index = 0; index < instructions.Count; index++)
		{
			var instruction = instructions[index];

			if (instruction.OpCode == OpCodes.Dup && instructions[index + 1].OpCode == OpCodes.Dup)
			{
				doubleDupInstruction = instructions[index + 1];
			}

			if (instruction.OpCode == OpCodes.Pop && doubleDupInstruction != null)
			{
				var extraPopInstruction = instructions[index];
				ilProcessor.Remove(extraPopInstruction);
				ilProcessor.InsertAfter(doubleDupInstruction, extraPopInstruction);
				doubleDupInstruction = null;
			}
		}

		body.InitLocals = true;
		body.OptimizeMacros();
	}

	private Instruction GetMethodBodyFirstInstruction()
	{
		return Method.IsConstructor
			? body.Instructions.First(i => i.OpCode == OpCodes.Call).Next
			: body.Instructions.First();
	}

	private IEnumerable<Instruction> GetStartInstructions()
	{
		return AddWriteStartEnd(StartMethod, true);
	}

	protected List<Instruction> GetParamInstructions()
	{
		/* TRACE ENTRY: 
		* What we'd like to achieve is this:
		* var paramNames = new string[] { "param1", "param2" }
		* var paramValues = new object[] { param1, param2 }
		* log.LogParams(title, paramNames, paramValues)
		* ...(existing code)...
		*/

		var instructions = new List<Instruction>();

		var traceEnterNeedsParamArray = body.Method.Parameters.Any(param => !param.IsOut);
		var traceEnterParamArraySize = body.Method.Parameters.Count(param => !param.IsOut);

		var stringArrayVar = new VariableDefinition(ModuleDefinition.Import(typeof(string[])));
		var objectArrayVar = new VariableDefinition(ModuleDefinition.Import(typeof(object[])));

		if (traceEnterNeedsParamArray)
		{
			instructions.Add(Instruction.Create(OpCodes.Ldc_I4, traceEnterParamArraySize)); //setArraySize
			instructions.Add(Instruction.Create(OpCodes.Newarr, String)); //create name array
			stringArrayVar = body.DeclareVariable("$stringParamArray", stringArrayVar.VariableType);
			instructions.Add(Instruction.Create(OpCodes.Stloc, stringArrayVar)); //store it in local variable

			instructions.Add(Instruction.Create(OpCodes.Ldc_I4, traceEnterParamArraySize)); //setArraySize
			instructions.Add(Instruction.Create(OpCodes.Newarr, Object)); //create name array
			objectArrayVar = body.DeclareVariable("$objectParamArray", objectArrayVar.VariableType);
			instructions.Add(Instruction.Create(OpCodes.Stloc, objectArrayVar)); //store it in local variable

			instructions.AddRange(BuildInstructionsToCopyParameterNamesAndValues(
				body.Method.Parameters.Where(p => !p.IsOut), stringArrayVar, objectArrayVar, 0));
		}
		else
		{
			return instructions;
		}

		//build up logger call

		if (IsInstanceLogger)
		{
			instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
		}

		instructions.Add(Instruction.Create(IsInstanceLogger ? OpCodes.Ldfld : OpCodes.Ldsfld, LoggerField));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, "Parameter Values"));
		instructions.Add(Instruction.Create(OpCodes.Ldloc, stringArrayVar));
		instructions.Add(Instruction.Create(OpCodes.Ldloc, objectArrayVar));
		instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 40));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
		instructions.Add(Instruction.Create(OpCodes.Ldnull));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, Method.DisplayName()));

		var lineNumber = 0;
		Method.Body.Instructions.Last()?.TryGetLineNumber(Method, false, out lineNumber);
		instructions.Add(Instruction.Create(OpCodes.Ldc_I4, lineNumber));

		instructions.Add(Instruction.Create(IsInstanceLogger ? OpCodes.Callvirt : OpCodes.Call, ParamsMethod));

		return instructions;
	}

	private IEnumerable<Instruction> GetCatchInstructions()
	{
		exceptionVariable = body.DeclareVariable("$exception", ExceptionType);
		yield return Instruction.Create(OpCodes.Stloc, exceptionVariable);

		if (IsInstanceLogger)
		{
			yield return Instruction.Create(OpCodes.Ldarg_0);
		}

		foreach (var instruction in AddWriteCatch(ExceptionMethod))
		{
			yield return instruction;
		}

		yield return Instruction.Create(OpCodes.Rethrow);
	}

	private IEnumerable<Instruction> AddWriteCatch(MethodReference writeMethod)
	{
		yield return Instruction.Create(IsInstanceLogger ? OpCodes.Ldfld : OpCodes.Ldsfld, LoggerField);
		yield return Instruction.Create(OpCodes.Ldloc, exceptionVariable);
		yield return Instruction.Create(OpCodes.Ldstr, "");
		yield return Instruction.Create(OpCodes.Ldstr, "");
		yield return Instruction.Create(OpCodes.Ldstr, "");
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldstr, Method.DisplayName());

		var lineNumber = 0;
		Method.Body.Instructions.Last()?.TryGetLineNumber(Method, false, out lineNumber);
		yield return Instruction.Create(OpCodes.Ldc_I4, lineNumber);

		yield return Instruction.Create(IsInstanceLogger ? OpCodes.Callvirt : OpCodes.Call, writeMethod);
	}

	private IEnumerable<Instruction> GetEndInstructions()
	{
		return AddWriteStartEnd(EndMethod, false);
	}

	private IEnumerable<Instruction> AddWriteStartEnd(MethodReference writeMethod, bool isStartLine)
	{
		if (IsInstanceLogger)
		{
			yield return Instruction.Create(OpCodes.Ldarg_0);
		}

		yield return Instruction.Create(IsInstanceLogger ? OpCodes.Ldfld : OpCodes.Ldsfld, LoggerField);
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldnull);
		yield return Instruction.Create(OpCodes.Ldstr, Method.DisplayName());

		var lineNumber = 0;
		(isStartLine ? Method.Body.Instructions.First() : Method.Body.Instructions.Last())?
			.TryGetLineNumber(Method, isStartLine, out lineNumber);
		yield return Instruction.Create(OpCodes.Ldc_I4, lineNumber);

		yield return Instruction.Create(IsInstanceLogger ? OpCodes.Callvirt : OpCodes.Call, writeMethod);
	}

	protected List<Instruction> GetReturnValueInstructions(VariableDefinition returnVariable)
	{
		var instructions = new List<Instruction>();

		var stringArrayVar = new VariableDefinition(ModuleDefinition.Import(typeof(string[])));
		var objectArrayVar = new VariableDefinition(ModuleDefinition.Import(typeof(object[])));

		var hasReturnValue = Method.ReturnType.MetadataType != MetadataType.Void;

		var traceLeaveNeedsParamArray = hasReturnValue
			|| body.Method.Parameters.Any(param => param.IsOut || param.ParameterType.IsByReference);
		var traceLeaveParamArraySize = body.Method.Parameters.Count(param => param.IsOut || param.ParameterType.IsByReference)
			+ (hasReturnValue ? 1 : 0);

		if (traceLeaveNeedsParamArray)
		{
			instructions.Add(Instruction.Create(OpCodes.Ldc_I4, traceLeaveParamArraySize)); //setArraySize
			instructions.Add(Instruction.Create(OpCodes.Newarr, String)); //create name array
			stringArrayVar = body.DeclareVariable("$stringReturnArray", stringArrayVar.VariableType);
			instructions.Add(Instruction.Create(OpCodes.Stloc, stringArrayVar)); //store it in local variable

			instructions.Add(Instruction.Create(OpCodes.Ldc_I4, traceLeaveParamArraySize)); //setArraySize
			instructions.Add(Instruction.Create(OpCodes.Newarr, Object)); //create name array
			objectArrayVar = body.DeclareVariable("$objectReturnArray", objectArrayVar.VariableType);
			instructions.Add(Instruction.Create(OpCodes.Stloc, objectArrayVar)); //store it in local variable

			if (hasReturnValue)
			{
				instructions.AddRange(StoreValueReadByInstructionsInArray(stringArrayVar, 0,
					Instruction.Create(OpCodes.Ldstr, "Return")));
				instructions.AddRange(StoreVariableInObjectArray(objectArrayVar, 0, returnVariable));
			}

			instructions.AddRange(
				BuildInstructionsToCopyParameterNamesAndValues(
					body.Method.Parameters.Where(p => p.IsOut || p.ParameterType.IsByReference),
					stringArrayVar, objectArrayVar, hasReturnValue ? 1 : 0));
		}
		else
		{
			return instructions;
		}

		if (IsInstanceLogger)
		{
			instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
		}

		instructions.Add(Instruction.Create(IsInstanceLogger ? OpCodes.Ldfld : OpCodes.Ldsfld, LoggerField));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, "Return Values"));
		instructions.Add(Instruction.Create(OpCodes.Ldloc, stringArrayVar));
		instructions.Add(Instruction.Create(OpCodes.Ldloc, objectArrayVar));
		instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 40));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
		instructions.Add(Instruction.Create(OpCodes.Ldnull));
		instructions.Add(Instruction.Create(OpCodes.Ldstr, Method.DisplayName()));

		var lineNumber = 0;
		Method.Body.Instructions.Last()?.TryGetLineNumber(Method, false, out lineNumber);
		instructions.Add(Instruction.Create(OpCodes.Ldc_I4, lineNumber));

		instructions.Add(Instruction.Create(IsInstanceLogger ? OpCodes.Callvirt : OpCodes.Call, ParamsMethod));

		return instructions;
	}

	protected IEnumerable<Instruction> InitArray(VariableDefinition arrayVar, int size, TypeReference type)
	{
		yield return Instruction.Create(OpCodes.Ldc_I4, size); //setArraySize
		yield return Instruction.Create(OpCodes.Newarr, type); //create name array
		yield return Instruction.Create(OpCodes.Stloc, arrayVar); //store it in local variable
	}

	protected IEnumerable<Instruction> BuildInstructionsToCopyParameterNamesAndValues(
		IEnumerable<ParameterDefinition> parameters,
		VariableDefinition paramNamesDef, VariableDefinition paramValuesDef, int startingIndex)
	{
		var instructions = new List<Instruction>();
		var index = startingIndex;
		foreach (var parameter in parameters)
		{
			//set name at index
			instructions.AddRange(StoreValueReadByInstructionsInArray(paramNamesDef, index,
				Instruction.Create(OpCodes.Ldstr, parameter.Name)));
			instructions.AddRange(StoreParameterInObjectArray(paramValuesDef, index, parameter));
			index++;
		}

		return instructions;
	}

	protected IEnumerable<Instruction> StoreValueReadByInstructionsInArray(VariableDefinition arrayVar, int position,
		params Instruction[] putValueOnStack)
	{
		yield return Instruction.Create(OpCodes.Ldloc, arrayVar);
		yield return Instruction.Create(OpCodes.Ldc_I4, position);
		foreach (var instruction in putValueOnStack)
		{
			yield return instruction;
		}
		yield return Instruction.Create(OpCodes.Stelem_Ref);
	}

	private IEnumerable<Instruction> StoreParameterInObjectArray(VariableDefinition arrayVar, int position,
		ParameterDefinition parameter)
	{
		var parameterType = parameter.ParameterType;
		var parameterElementType = parameterType.IsByReference ? ((ByReferenceType) parameterType).ElementType : parameterType;
		yield return Instruction.Create(OpCodes.Ldloc, arrayVar);
		yield return Instruction.Create(OpCodes.Ldc_I4, position);
		yield return Instruction.Create(OpCodes.Ldarg, parameter);

		//check if ref (or out)
		if (parameterType.IsByReference)
		{
			switch (parameterElementType.MetadataType)
			{
				case MetadataType.ValueType:
					yield return Instruction.Create(OpCodes.Ldobj, parameterElementType);
					break;
				case MetadataType.Int16:
					yield return Instruction.Create(OpCodes.Ldind_I2);
					break;
				case MetadataType.Int32:
					yield return Instruction.Create(OpCodes.Ldind_I4);
					break;
				case MetadataType.Int64:
				case MetadataType.UInt64:
					yield return Instruction.Create(OpCodes.Ldind_I8);
					break;
                case MetadataType.Char:
				case MetadataType.UInt16:
					yield return Instruction.Create(OpCodes.Ldind_U2);
					break;
				case MetadataType.UInt32:
					yield return Instruction.Create(OpCodes.Ldind_U4);
					break;
				case MetadataType.Single:
					yield return Instruction.Create(OpCodes.Ldind_R4);
					break;
				case MetadataType.Double:
					yield return Instruction.Create(OpCodes.Ldind_R8);
					break;
				case MetadataType.IntPtr:
                case MetadataType.UIntPtr:
					yield return Instruction.Create(OpCodes.Ldind_I);
					break;
				case MetadataType.Boolean:
				case MetadataType.SByte:
					yield return Instruction.Create(OpCodes.Ldind_I1);
					break;
				case MetadataType.Byte:
					yield return Instruction.Create(OpCodes.Ldind_U1);
					break;
				default:
					yield return Instruction.Create(OpCodes.Ldind_Ref);
					break;
			}
		}

		//box if necessary
		if (IsBoxingNeeded(parameterElementType))
		{
			yield return Instruction.Create(OpCodes.Box, parameterElementType);
		}
		yield return Instruction.Create(OpCodes.Stelem_Ref);
	}

	protected IEnumerable<Instruction> StoreVariableInObjectArray(VariableDefinition arrayVar, int position,
		VariableDefinition variable)
	{
		var varType = variable.VariableType;
		yield return Instruction.Create(OpCodes.Ldloc, arrayVar);
		yield return Instruction.Create(OpCodes.Ldc_I4, position);
		yield return Instruction.Create(OpCodes.Ldloc, variable);
		//box if necessary
		if (IsBoxingNeeded(varType))
		{
			yield return Instruction.Create(OpCodes.Box, varType);
		}
		yield return Instruction.Create(OpCodes.Stelem_Ref);
	}

	protected static bool IsBoxingNeeded(TypeReference type)
	{
		return type.IsPrimitive || type.IsGenericParameter || type.IsValueType;
	}
}
