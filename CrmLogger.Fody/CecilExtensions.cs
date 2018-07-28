#region Imports

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

#endregion

public static class CecilExtensions
{
	public static void Replace(this Collection<Instruction> collection, Instruction instruction,
		ICollection<Instruction> instructions)
	{
		var newInstruction = instructions.First();
		instruction.Operand = newInstruction.Operand;
		instruction.OpCode = newInstruction.OpCode;

		var indexOf = collection.IndexOf(instruction);
		foreach (var instruction1 in instructions.Skip(1))
		{
			collection.Insert(indexOf + 1, instruction1);
			indexOf++;
		}
	}

	public static void Append(this List<Instruction> collection, params Instruction[] instructions)
	{
		collection.AddRange(instructions);
	}

	public static string DisplayName(this MethodDefinition method)
	{
		method = GetActualMethod(method);
		var paramNames = string.Join(", ", method.Parameters.Select(x => $"{x.ParameterType.DisplayName()} {x.Name}"));
		return $"{method.DeclaringType.DisplayName()}.{method.Name}({paramNames}) : {method.ReturnType.DisplayName()}";
	}

	public static string DisplayName(this TypeReference typeReference)
	{
		var genericInstanceType = typeReference?.Resolve()?.GetGeneric() as GenericInstanceType;

		if (genericInstanceType != null && genericInstanceType.HasGenericArguments)
		{
			return typeReference.Name.Split('`').First() + "<"
				+ string.Join(", ", genericInstanceType.GenericArguments.Select(c => c.DisplayName())) + ">";
		}

		return typeReference?.Name;
	}

	static MethodDefinition GetActualMethod(MethodDefinition method)
	{
		var isTypeCompilerGenerated = method.DeclaringType.IsCompilerGenerated();
		if (isTypeCompilerGenerated)
		{
			var rootType = method.DeclaringType.GetNonCompilerGeneratedType();
			if (rootType != null)
			{
				foreach (var parentClassMethod in rootType.Methods)
				{
					if (method.DeclaringType.Name.Contains("<" + parentClassMethod.Name + ">"))
					{
						return parentClassMethod;
					}
					if (method.Name.StartsWith("<" + parentClassMethod.Name + ">"))
					{
						return parentClassMethod;
					}
				}
			}
		}

		var isMethodCompilerGenerated = method.IsCompilerGenerated();
		if (isMethodCompilerGenerated)
		{
			foreach (var parentClassMethod in method.DeclaringType.Methods)
			{
				if (method.Name.StartsWith("<" + parentClassMethod.Name + ">"))
				{
					return parentClassMethod;
				}
			}
		}
		return method;
	}

	public static TypeDefinition GetNonCompilerGeneratedType(this TypeDefinition typeDefinition)
	{
		while (typeDefinition.IsCompilerGenerated() && typeDefinition.DeclaringType != null)
		{
			typeDefinition = typeDefinition.DeclaringType;
		}
		return typeDefinition;
	}

	public static bool IsCompilerGenerated(this ICustomAttributeProvider value)
	{
		return value.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
	}

	public static bool IsCompilerGenerated(this TypeDefinition typeDefinition)
	{
		return typeDefinition.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute") ||
			(typeDefinition.IsNested && typeDefinition.DeclaringType.IsCompilerGenerated());
	}

	public static void CheckForInvalidLogToUsages(this MethodDefinition methodDefinition)
	{
		foreach (var instruction in methodDefinition.Body.Instructions)
		{
			var methodReference = instruction.Operand as MethodReference;
			if (methodReference != null)
			{
				var declaringType = methodReference.DeclaringType;
				if (declaringType.Name != "LogTo")
				{
					continue;
				}
				if (declaringType.Namespace == null || !declaringType.Namespace.StartsWith("Anotar"))
				{
					continue;
				}
				//TODO: sequence point
				if (instruction.OpCode == OpCodes.Ldftn)
				{
					var message = $"Inline delegate usages of 'LogTo' are not supported. '{methodDefinition.FullName}'.";
					throw new WeavingException(message);
				}
			}
			var typeReference = instruction.Operand as TypeReference;
			if (typeReference != null)
			{
				if (typeReference.Name != "LogTo")
				{
					continue;
				}
				if (typeReference.Namespace == null || !typeReference.Namespace.StartsWith("Anotar"))
				{
					continue;
				}
				//TODO: sequence point
				if (instruction.OpCode == OpCodes.Ldtoken)
				{
					var message =
						$"'typeof' usages or passing `dynamic' params to 'LogTo' are not supported. '{methodDefinition.FullName}'.";
					throw new WeavingException(message);
				}
			}
		}
	}

	public static MethodDefinition GetStaticConstructor(this TypeDefinition type)
	{
		var staticConstructor = type.Methods.FirstOrDefault(x => x.IsConstructor && x.IsStatic);
		if (staticConstructor == null)
		{
			const MethodAttributes attributes = MethodAttributes.Static
				| MethodAttributes.SpecialName
				| MethodAttributes.RTSpecialName
				| MethodAttributes.HideBySig
				| MethodAttributes.Private;
			staticConstructor = new MethodDefinition(".cctor", attributes, type.Module.TypeSystem.Void);

			staticConstructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			type.Methods.Add(staticConstructor);
		}
		staticConstructor.Body.InitLocals = true;
		return staticConstructor;
	}

	public static void InsertBefore(this ILProcessor processor, Instruction target, IEnumerable<Instruction> instructions)
	{
		foreach (var instruction in instructions)
		{
			processor.InsertBefore(target, instruction);
		}
	}


	public static bool IsBasicLogCall(this Instruction instruction)
	{
		var previous = instruction.Previous;
		if (previous.OpCode != OpCodes.Newarr || ((TypeReference) previous.Operand).FullName != "System.Object")
		{
			return false;
		}

		previous = previous.Previous;
		if (previous.OpCode != OpCodes.Ldc_I4)
		{
			return false;
		}

		previous = previous.Previous;
		if (previous.OpCode != OpCodes.Ldstr)
		{
			return false;
		}

		return true;
	}

	public static Instruction FindStringInstruction(this Instruction call)
	{
		if (IsBasicLogCall(call))
		{
			return call.Previous.Previous.Previous;
		}

		var previous = call.Previous;
		if (previous.OpCode != OpCodes.Ldloc)
		{
			return null;
		}

		var variable = (VariableDefinition) previous.Operand;

		while (previous != null && (previous.OpCode != OpCodes.Stloc || previous.Operand != variable))
		{
			previous = previous.Previous;
		}

		if (previous == null)
		{
			return null;
		}

		if (IsBasicLogCall(previous))
		{
			return previous.Previous.Previous.Previous;
		}

		return null;
	}

	public static bool TryGetLineNumber(this Instruction instruction, MethodDefinition method, bool isStartLine,
		out int lineNumber)
	{
		while (true)
		{
			var sequencePoints = method.DebugInformation.SequencePoints;

			if (isStartLine)
				// not a hidden line http://blogs.msdn.com/b/jmstall/archive/2005/06/19/feefee-sequencepoints.aspx
			{
				if (sequencePoints.First().StartLine != 0xFeeFee)
				{
					lineNumber = sequencePoints.First().StartLine;
					return true;
				}
			}

			if (!isStartLine)
				// not a hidden line http://blogs.msdn.com/b/jmstall/archive/2005/06/19/feefee-sequencepoints.aspx
			{
				if (sequencePoints.Last().StartLine != 0xFeeFee)
				{
					lineNumber = sequencePoints.Last().StartLine;
					return true;
				}
			}

			instruction = instruction.Previous;
			if (instruction == null)
			{
				lineNumber = 0;
				return false;
			}
		}
	}

	public static bool ContainsAttribute(this Collection<CustomAttribute> attributes, string attributeName)
	{
		var containsAttribute = attributes.FirstOrDefault(x => x.AttributeType.FullName == attributeName);
		if (containsAttribute != null)
		{
			attributes.Remove(containsAttribute);
		}
		return containsAttribute != null;
	}

	public static MethodDefinition FindMethod(this TypeDefinition typeDefinition, string method, params string[] paramTypes)
	{
		var firstOrDefault = typeDefinition.Methods
			.FirstOrDefault(x =>
				!x.HasGenericParameters &&
					x.Name == method &&
					x.IsMatch(paramTypes));
		if (firstOrDefault == null)
		{
			var parameterNames = string.Join(", ", paramTypes);
			throw new WeavingException(
				$"Expected to find method '{method}({parameterNames})' on type '{typeDefinition.FullName}'.");
		}
		return firstOrDefault;
	}

	public static MethodDefinition FindGenericMethod(this TypeDefinition typeDefinition, string method,
		params string[] paramTypes)
	{
		var firstOrDefault = typeDefinition.Methods
			.FirstOrDefault(x =>
				x.HasGenericParameters &&
					x.Name == method &&
					x.IsMatch(paramTypes));
		if (firstOrDefault == null)
		{
			var parameterNames = string.Join(", ", paramTypes);
			throw new WeavingException(
				$"Expected to find method '{method}({parameterNames})' on type '{typeDefinition.FullName}'.");
		}
		return firstOrDefault;
	}

	public static bool IsMatch(this MethodReference methodReference, params string[] paramTypes)
	{
		if (methodReference.Parameters.Count != paramTypes.Length)
		{
			return false;
		}
		for (var index = 0; index < methodReference.Parameters.Count; index++)
		{
			var parameterDefinition = methodReference.Parameters[index];
			var paramType = paramTypes[index];
			if (parameterDefinition.ParameterType.Name != paramType)
			{
				return false;
			}
		}
		return true;
	}

	public static FieldReference GetGeneric(this FieldDefinition definition)
	{
		if (definition.DeclaringType.HasGenericParameters)
		{
			var declaringType = new GenericInstanceType(definition.DeclaringType);
			foreach (var parameter in definition.DeclaringType.GenericParameters)
			{
				declaringType.GenericArguments.Add(parameter);
			}
			return new FieldReference(definition.Name, definition.FieldType, declaringType);
		}

		return definition;
	}

	public static TypeReference GetGeneric(this TypeDefinition definition)
	{
		if (definition.HasGenericParameters)
		{
			var genericInstanceType = new GenericInstanceType(definition);
			foreach (var parameter in definition.GenericParameters)
			{
				genericInstanceType.GenericArguments.Add(parameter);
			}
			return genericInstanceType;
		}

		return definition;
	}

	public static VariableDefinition DeclareVariable(this MethodBody body, string name, TypeReference type)
	{
		var variable = new VariableDefinition(type);
		body.Variables.Add(variable);
		var variableDebug = new VariableDebugInformation(variable, name);
		body.Method?.DebugInformation?.Scope?.Variables?.Add(variableDebug);
		return variable;
	}

	/// <summary>
	/// Author: Ahmed el-Sawalhy<br />
	/// Credit: http://blog.stevesindelar.cz/mono-cecil-how-to-get-all-base-types-and-interfaces-with-resolved-generic-arguments
	/// </summary>
	public static FieldReference GetLoggerWithProperType(this TypeDefinition type, TypeDefinition loggerTypeDefinition)
	{
		var current = type;
		var mappedFromSuperType = new List<TypeReference>();
		var previousGenericArgsMap =
			GetGenericArgsMap(type, new Dictionary<string, TypeReference>(), mappedFromSuperType);

		var returnValue = type.GetGeneric();

		do
		{
			var loggerField = current.Fields.FirstOrDefault(x => x.FieldType.FullName == loggerTypeDefinition.FullName);

			if (loggerField != null)
			{
				return new FieldReference(loggerField.Name, loggerField.FieldType, returnValue);
			}

			var currentBase = current.BaseType;

			if (currentBase is GenericInstanceType)
			{
				previousGenericArgsMap =
					GetGenericArgsMap(currentBase, previousGenericArgsMap, mappedFromSuperType);

				if (mappedFromSuperType.Any())
				{
					currentBase = ((GenericInstanceType)currentBase)
						.ElementType.MakeGenericInstanceType(
							previousGenericArgsMap.Select(x => x.Value).ToArray());

					mappedFromSuperType.Clear();
				}
			}
			else
			{
				previousGenericArgsMap = new Dictionary<string, TypeReference>();
			}

			returnValue = currentBase;
			current = current.BaseType.Resolve();
		} while (!current.FullName.Equals(typeof(object).FullName));

		return null;
	}

	private static IDictionary<string, TypeReference> GetGenericArgsMap(
		TypeReference type, IDictionary<string, TypeReference> superTypeMap,
		IList<TypeReference> mappedFromSuperType)
	{
		var result = new Dictionary<string, TypeReference>();

		if (type is GenericInstanceType == false)
		{
			return result;
		}

		var genericArgs = ((GenericInstanceType)type).GenericArguments;
		var genericPars = ((GenericInstanceType)type)
			.ElementType.Resolve().GenericParameters;

		/*
		 * Now genericArgs contain concrete arguments for the generic
* parameters (genericPars).
		 *
		 * However, these concrete arguments don't necessarily have
* to be concrete TypeReferences, these may be referencec to
* generic parameters from super type.
		 *
		 * Example:
		 *
		 *      Consider following hierarchy:
		 *          StringMap<T> : Dictionary<string, T>
		 *
		 *          StringIntMap : StringMap<int>
		 *
		 *      What would happen if we walk up the hierarchy from StringIntMap:
		 *          -> StringIntMap
		 *              - here dont have any generic agrs or params for StringIntMap.
		 *              - but when we reesolve StringIntMap we get a
*					reference to the base class StringMap<int>,
		 *          -> StringMap<int>
		 *              - this reference will have one generic argument
*					System.Int32 and it's ElementType,
		 *                which is StringMap<T>, has one generic argument 'T'.
		 *              - therefore we need to remember mapping T to System.Int32
		 *              - when we resolve this class we'll get StringMap<T> and it's base
		 *              will be reference to Dictionary<string, T>
		 *          -> Dictionary<string, T>
		 *              - now *genericArgs* will be System.String and 'T'
		 *              - genericPars will be TKey and TValue from Dictionary
* 					declaration Dictionary<TKey, TValue>
		 *              - we know that TKey is System.String and...
		 *              - because we have remembered a mapping from T to
*					System.Int32 and now we see a mapping from TValue to T,
		 *              	we know that TValue is System.Int32, which bring us to
*					conclusion that StringIntMap is instance of
		 *          -> Dictionary<string, int>
		 */

		for (var i = 0; i < genericArgs.Count; i++)
		{
			var arg = genericArgs[i];
			var param = genericPars[i];

			if (arg is GenericParameter)
			{
				var isFound = superTypeMap.TryGetValue(arg.Name, out var mapping);

				if (isFound == false)
				{
					mapping = arg;
				}

				//if (superTypeMap.TryGetValue(arg.Name, out var mapping) == false)
				//{
				//	//throw new Exception(
				//	//	string.Format(
				//	//		"GetGenericArgsMap: A mapping from supertype was not found. " +
				//	//			"Program searched for generic argument of name {0} in supertype generic arguments map " +
				//	//			"as it should server as value form generic argument for generic parameter {1} in the type {2}",
				//	//		arg.Name, param.Name, type.FullName));
				//}

				mappedFromSuperType.Add(mapping);
				result.Add(param.Name, mapping);
			}
			else
			{
				result.Add(param.Name, arg);
			}
		}

		return result;
	}

	public static bool IsAnonymous(this MethodDefinition method)
	{
		return Regex.IsMatch(method.Name, "^<\\w*?>\\w*?__\\w*?_\\d*");
	}
}
