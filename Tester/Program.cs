using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Tester
{
	class Program
	{
		static string assemblyPath = Path.Combine(@"D:\Projects\General\Code\ConsoleApp2\ConsoleApp2", @"bin\Debug\ConsoleApp2.exe");

		static void Main(string[] args)
		{
#if (!DEBUG)
			assemblyPath = assemblyPath.Replace("Debug", "Release");
#endif

			var newAssemblyPath = WeaverHelper.Weave(assemblyPath);
			var assembly = Assembly.LoadFile(newAssemblyPath);
			var type = assembly.GetType("ConsoleApp2.PluginName1Logic1");
			var instance = (dynamic)Activator.CreateInstance(type);
			instance.ExecuteLogic();
		}
	}
}
