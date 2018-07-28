#region Imports

using System;
using LinkDev.Libraries.Common;

#endregion

namespace ConsoleApp1
{
	class Program
	{
		static CrmLog log = new CrmLog(true, LogLevel.Debug);

		static void Main(string[] args)
		{
			TestLogStatic.TestNoLog(1, 2);
			TestLogStatic.TestNormal(1, 2);
			TestLogStatic.TestRef(new Exception("TEST!"));
			TestLogStatic.TestNullRef(null);
			TestLogStatic.TestTuple('a', true, new[] {1.5, 2.1, 3.7});
			TestLogStatic.TestOut(out var x);
			TestLogStatic.TestEmpty(1, 2);
			try
			{
				TestLogStatic.TestException(1, 2);
			}
			catch
			{ }

			TestNoTagStatic.TestNoLog(1, 2);
			TestNoTagStatic.TestLog(1, 2);
			TestNoTagStatic.TestEmpty(1, 2);
			TestNoTagStatic.TestLogEmpty(1, 2);
			try
			{
				TestNoTagStatic.TestLogException(1, 2);
			}
			catch
			{ }

			TestNoLogStatic.TestNoLog(1, 2);
			TestNoLogStatic.TestLog(1, 2);
			TestNoLogStatic.TestEmpty(1, 2);
			TestNoLogStatic.TestLogEmpty(1, 2);
			try
			{
				TestNoLogStatic.TestLogException(1, 2);
			}
			catch
			{ }

			var test13 = new TestLogInstance();
				test13.TestNormal(1, 2);
			test13.TestNoLog(1, 2);
			test13.TestEmpty(1, 2);
			try
			{
				test13.TestReturnThis(1, 2);
				test13.TestException(1, 2);
			}
			catch
			{ }

			Console.ReadKey();
		}
	}

	[Log]
	class TestLogStatic
	{
		static CrmLog log = new CrmLog(true, LogLevel.Debug);

		[NoLog]
		public static int TestNoLog(int i, int x)
		{
			return i + x;
		}

		public static Exception TestRef(Exception i)
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

		public static (char i, bool x, double[] j) TestTuple(char i, bool x, double[] j)
		{
			return (i, x, j);
		}

		public static (char i, int x, string j) TestOut(out int x)
		{
			return ('y', x = 1, "TEST!");
		}

		public static void TestEmpty(int i, int x)
		{ }

		public static int TestException(int i, int x)
		{
			throw new Exception("TEST!");
		}
	}

	class TestNoTagStatic
	{
		static CrmLog log = new CrmLog(true, LogLevel.Debug);

		[NoLog]
		public static int TestNoLog(int i, int x)
		{
			return i + x;
		}

		[Log]
		public static int TestLog(int i, int x)
		{
			return i + x;
		}

		public static void TestEmpty(int i, int x)
		{ }

		[Log]
		public static void TestLogEmpty(int i, int x)
		{ }

		[Log]
		public static int TestLogException(int i, int x)
		{
			throw new Exception("TEST!");
		}
	}

	[NoLog]
	class TestNoLogStatic
	{
		static CrmLog log = new CrmLog(true, LogLevel.Debug);

		[NoLog]
		public static int TestNoLog(int i, int x)
		{
			return i + x;
		}

		[Log]
		public static int TestLog(int i, int x)
		{
			return i + x;
		}

		public static void TestEmpty(int i, int x)
		{ }

		[Log]
		public static void TestLogEmpty(int i, int x)
		{ }

		[Log]
		public static int TestLogException(int i, int x)
		{
			throw new Exception("TEST!");
		}
	}

	[Log]
	class TestLogInstance
	{
		CrmLog log = new CrmLog(true, LogLevel.Debug);

		public TestLogInstance()
		{ }

		[NoLog]
		public int TestNoLog(int i, int x)
		{
			return i + x;
		}

		public int TestNormal(int i, int x)
		{
			return i + x;
		}

		public TestLogInstance TestReturnThis(int i, int x)
		{
			TestException(i, x);
			return this;
		}

		public void TestEmpty(int i, int x)
		{}

		public int TestException(int i, int x)
		{
			throw new Exception("TEST!");
		}
	}
}
