using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkDev.Libraries.Common;
using Microsoft.Xrm.Sdk;

namespace ConsoleApp2
{
	class Program
	{
		static void Main(string[] args)
		{
			//new TestLogStatic().TestInstance(new Exception("TEST!"));
			//TestLogStatic.TestNoLog(1, 2);
			//TestLogStatic.TestNormal(1, 2);
			//TestLogStatic.TestNormal(null);
			TestLogStatic.TestNormal(
				new Dictionary<string, int>
									 {
										 ["TEST12"] = 123,
										 ["TEST13"] = 1243,
										 ["TEST14"] = 12633,
										 ["TEST15"] = 1253,
										 ["TEST16"] = 1323,
										 ["TEST17"] = 123,
										 ["TEST18"] = 1283,
										 ["TEST19"] = 1123,
										 ["TEST111"] = 2,
										 ["TEST122"] = 4,
										 ["TEST133"] = 563,
										 ["TEST144"] = 863,
										 ["TEST155"] = 198,
										 ["TEST166"] = 46,
										 ["TEST177"] = 967,
										 ["TEST188"] = 2423,
										 ["TEST199"] = 7,
										 ["TEST1111"] = 345,
										 ["TEST1222"] = 12,
										 ["TEST1333"] = 435,
										 ["TEST1444"] = 565,
										 ["TEST1555"] = 657,
										 ["TEST1666"] = 126388,
										 ["Test2"] = 568
									 });
			//TestLogStatic.TestRef(new Exception("TEST!"));
			//TestLogStatic.TestNullRef(null);
			//TestLogStatic.TestTuple('a', true, new List<double>(new [] {1, 2, 3, 4.0, 7.0, 4.8, 15.0, 97.1, 3734, 85, 123, 5, 2357, 163, 112, 765}));
			//TestLogStatic.TestSingleTuple('a', out var u);
			//TestLogStatic.TestOut(out var x);
			//TestLogStatic.TestEmpty(1, 2);
			//try
			//{
			//	TestLogStatic.TestException(1, 2);
			//}
			//catch
			//{
			//}

			Console.ReadKey();
		}
	}

	[Log]
	class TestLogStatic
	{
		static CrmLog log = new CrmLog(true, LogLevel.Debug);

		public static int TestNoLog(int i, int x)
		{
			return i + x;
		}

		public static Exception TestRef(Exception i)
		{
			return i;
		}

		public Exception TestInstance(Exception i)
		{
			return i;
		}

		public static Exception TestNullRef(Exception i)
		{
			return i;
		}

		public static int TestNormal(int i, int x)
		{
			return i + x;
		}

		//public static EntityReference TestNormal(EntityReference reference)
		//{
		//	return reference;
		//}

		public static IDictionary<string, int> TestNormal(IDictionary<string, int> dict)
		{
			return dict;
		}

		public static (char i, bool x, List<double> y) TestTuple(char i, bool x, List<double> y)
		{
			return (i, x, y);
		}

		public static (char i, bool j) TestSingleTuple(char i, out bool j)
		{
			return (i, j = true);
		}

		public static void TestOut(out char x)
		{
			x = 'o';
		}

		public static void TestEmpty(int i, int x)
		{
		}

		public static int TestException(int i, int x)
		{
			throw new Exception("TEST!");
		}
	}

	[Log]
	internal class PluginName1Logic1 : PluginName1Logic2<Helpers, OutOfMemoryException, IEnumerable<Exception>>
	{
		public PluginName1Logic1(IServiceProvider serviceProvider) : base(serviceProvider)
		{
			log = new CrmLog(serviceProvider);
			log.LogExecutionStart();
		}

		public void ExecuteLogic()
		{
			log.LogLine();
		}
	}

	internal class PluginName1Logic2<A, B, C> : PluginName1Logic3<Helpers, OutOfMemoryException, IEnumerable<Exception>> where A : class where B : class where C : IEnumerable<Exception>
	{
		public PluginName1Logic2(IServiceProvider serviceProvider)
		{
		}
	}

	internal class PluginName1Logic3<T, Y, Z> where T : class where Y : class where Z : IEnumerable<Exception>
	{
		public CrmLog log;
	}

}
