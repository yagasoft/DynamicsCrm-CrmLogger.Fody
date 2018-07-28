#region Imports

using System;
using System.Activities;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;

#endregion

// Version: 1.12.4

namespace LinkDev.Libraries.Common
{
	public class LogAttribute : Attribute
	{ }

	public class NoLogAttribute : Attribute
	{ }

	#region CrmLog

	/// <summary>
	///     Receives log entries and saves them to CRM, disk, or output to console.<br />
	///     Author: Ahmed el-Sawalhy<br />
	///     Version: 3.4.1
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public class CrmLog
	{
		/// <summary>
		///     The end states of the program that contains the logging object
		/// </summary>
		public enum ExecutionEndState
		{
			Success = 0,
			Failure = 1
		}

		#region Instance variables

		public IOrganizationService OrganizationService { get; private set; }

		private PrivateConfiguration cachedConfig;
		private const string CONFIG_MEM_CACHE_KEY = "CrmLoggerConfig";

		public Guid UserId { get; private set; }
		private readonly bool isSandboxMode;
		private readonly bool isNoneMode;
		private readonly bool isPlugin;

		public FileConfiguration Config { get; private set; }
		public LogLevel MaxLogLevel { get; private set; }

		private Entity parentLog;

		public ExecutionEndState AssemblyExecutionState { get; private set; }
		private bool exceptionThrown;
		private LogEntry exceptionLogEntry;

		public int CurrentEntryIndex { get; private set; }

		private Queue<LogEntry> logQueue;
		private Queue<LogEntry> offlineQueue;
		private Stack<LogEntry> execSeqStack;

		public DateTime LogStartDate { get; private set; }
		public bool ExecutionStarted { get; private set; }
		private Stopwatch executionTimer;
		private Stack<Stopwatch> functionTimersStack;
		private Stack<int> durationsStack;

		private bool isConsoleEnabled;
		private bool isAutoLogToConsole;

		public string OfflinePath { get; private set; }
		public bool IsOfflineEnabled { get; private set; }
		public bool IsFailOver { get; private set; }
		public bool IsOfflineOnly { get; private set; }

		public LogLevel MaxConsoleLogLevel { get; private set; }

		public bool IsConsoleEnabled
		{
			get { return isConsoleEnabled; }
			private set
			{
				isConsoleEnabled = value;
				isAutoLogToConsole = isConsoleEnabled && IsAutoLogToConsole;
			}
		}

		public bool IsAutoLogToConsole
		{
            get { return isAutoLogToConsole; }
			private set
			{
				isAutoLogToConsole = value;
				isConsoleEnabled = isAutoLogToConsole || IsConsoleEnabled;
			}
		}

		private bool isParentCreated;
		private bool? isBulkPreviously;

		#endregion

		#region Init

		/// <summary>
		///     Constructor!
		/// </summary>
		/// <param name="maximumLevel">[Optional] The maximum logging level to use, above which, no logs will be saved</param>
		public CrmLog(LogLevel? maximumLevel = null, [CallerMemberName] string callingFunction = "")
		{
			InitialiseLog(null, maximumLevel, callingFunction);
			IsOfflineOnly = true;
		}

		/// <summary>
		///     Constructor!
		/// </summary>
		/// <param name="organizationService">The service to be used to flush the entries to CRM</param>
		/// <param name="maximumLevel">[Optional] The maximum logging level to use, above which, no logs will be saved</param>
		public CrmLog(IOrganizationService organizationService, LogLevel? maximumLevel = null,
			[CallerMemberName] string callingFunction = "")
		{
			InitialiseLog(organizationService, maximumLevel, callingFunction);
		}

		/// <summary>
		///     Constructor!
		/// </summary>
		/// <param name="organizationService">The service to be used to flush the entries to CRM</param>
		/// <param name="userId">The ID of the user that is going to be set in the logs</param>
		/// <param name="maximumLevel">[Optional] The maximum logging level to use, above which, no logs will be saved</param>
		public CrmLog(IOrganizationService organizationService, Guid userId,
			LogLevel? maximumLevel = null, [CallerMemberName] string callingFunction = "")
		{
			UserId = userId;
			InitialiseLog(organizationService, maximumLevel, callingFunction);
		}

		/// <summary>
		///     Initializes a new instance of the <see cref="CrmLog" /> class.
		/// </summary>
		/// <param name="serviceProvider">The service provider from a plugin.</param>
		/// <param name="maximumLevel">[OPTIONAL] The maximum level.</param>
		public CrmLog(IServiceProvider serviceProvider, LogLevel? maximumLevel = null,
			[CallerMemberName] string callingFunction = "")
		{
			var context = (IPluginExecutionContext) serviceProvider.GetService(typeof(IPluginExecutionContext));
			UserId = context.InitiatingUserId;
			isSandboxMode = context.IsolationMode == 2;
			isNoneMode = context.IsolationMode != 2;
			isPlugin = true;

			InitialiseLog(((IOrganizationServiceFactory)
				serviceProvider.GetService(typeof(IOrganizationServiceFactory))).CreateOrganizationService(null),
				maximumLevel, callingFunction);
		}

		/// <summary>
		///     Initializes a new instance of the <see cref="CrmLog" /> class.
		/// </summary>
		/// <param name="activityContext">The activity context from a custom step.</param>
		/// <param name="maximumLevel">[OPTIONAL] The maximum level.</param>
		public CrmLog(ActivityContext activityContext, LogLevel? maximumLevel = null,
			[CallerMemberName] string callingFunction = "")
		{
			var context = activityContext.GetExtension<IWorkflowContext>();
			UserId = context.InitiatingUserId;
			isSandboxMode = context.IsolationMode == 2;
			isNoneMode = context.IsolationMode != 2;
			isPlugin = true;

			InitialiseLog(activityContext.GetExtension<IOrganizationServiceFactory>()
				.CreateOrganizationService(null), maximumLevel, callingFunction);
		}

		/// <summary>
		///     Initializes a new instance of the <see cref="CrmLog" /> class.
		/// </summary>
		/// <param name="path">[OPTIONAL] The path.</param>
		/// <param name="maximumLevel">[OPTIONAL] The maximum level.</param>
		/// <param name="config">[OPTIONAL] Parameters to use in managing offline logging.</param>
		public CrmLog(string path = "", LogLevel? maximumLevel = null, FileConfiguration config = null,
			[CallerMemberName] string callingFunction = "")
		{
			IsOfflineOnly = true;
			InitialiseLog(null, maximumLevel, callingFunction);
			InitOfflineLog(path, false, config);
		}

		/// <summary>
		///     Initializes a new instance of the <see cref="CrmLog" /> class.
		/// </summary>
		/// <param name="isAutoLog">If 'true', automatically output log entries to the console.</param>
		/// <param name="maxLogLevel">[OPTIONAL] The maximum log level to output to console. Default is 'Info'.</param>
		public CrmLog(bool isAutoLog, LogLevel? maxLogLevel = null, [CallerMemberName] string callingFunction = "")
		{
			IsOfflineOnly = true;
			InitialiseLog(null, maxLogLevel, callingFunction);
			MaxConsoleLogLevel = maxLogLevel ?? LogLevel.Info;
			IsConsoleEnabled = true;
			IsAutoLogToConsole = isAutoLog;
		}

		private PrivateConfiguration GetConfiguration()
		{
			try
			{
				if (OrganizationService != null)
				{
					var config = Helpers.GetFromMemCache<Entity>(CONFIG_MEM_CACHE_KEY);

					if (config == null)
					{
						config = new OrganizationServiceContext(OrganizationService)
							.CreateQuery("ldv_genericconfiguration")
							.FirstOrDefault(configQ => configQ.GetAttributeValue<OptionSetValue>("ldv_loglevel") != null);

						Helpers.AddToMemCache(CONFIG_MEM_CACHE_KEY, config, DateTimeOffset.Now.AddMinutes(5));
					}

					if (config != null)
					{
						return cachedConfig =
							new PrivateConfiguration
							{
								LogLevel = (LogLevel) (config.GetAttributeValue<OptionSetValue>("ldv_loglevel")
									?? new OptionSetValue((int) LogLevel.Warning)).Value,
								LogMode = (LogMode) (config.GetAttributeValue<OptionSetValue>("ldv_logmode")
									?? new OptionSetValue((int) LogMode.Crm)).Value,
								LogPath = config.GetAttributeValue<string>("ldv_logpath"),
								MaxFileSize = config.GetAttributeValue<int?>("ldv_maxfilesize"),
								FileDateFormat = config.GetAttributeValue<string>("ldv_logfiledateformat"),
								FileSplitMode = config.GetAttributeValue<OptionSetValue>("ldv_logfilesplitmode") == null
									? null
									: (SplitMode?) config.GetAttributeValue<OptionSetValue>("ldv_logfilesplitmode").Value,
								FileSplitFrequency = config.GetAttributeValue<OptionSetValue>("ldv_logfilesplitfrequency") == null
									? null
									: (SplitFrequency?) config.GetAttributeValue<OptionSetValue>("ldv_logfilesplitfrequency").Value,
								FileSplitDate = config.GetAttributeValue<DateTime>("ldv_logfilesplitdate"),
								CategoriseByType = config.GetAttributeValue<bool?>("ldv_iscategorisebytype")
							};
					}
				}
			}
			catch
			{
				// ignored
			}

			return null;
		}

		/// <summary>
		///     Initialises the CRM logger.
		/// </summary>
		/// <param name="organizationService">The service to be used to flush the entries to CRM</param>
		/// <param name="maximumLevel">[OPTIONAL] The maximum logging level to use, above which, no logs will be saved</param>
		private void InitialiseLog(IOrganizationService organizationService,
			LogLevel? maximumLevel = null, [CallerMemberName] string callingFunction = "")
		{
			logQueue = new Queue<LogEntry>();
			offlineQueue = new Queue<LogEntry>();
			execSeqStack = new Stack<LogEntry>();

			functionTimersStack = new Stack<Stopwatch>();
			durationsStack = new Stack<int>();

			OrganizationService = organizationService;

			LogStartDate = DateTime.UtcNow;
			CurrentEntryIndex = 1;

			var config = GetConfiguration();
			MaxLogLevel = maximumLevel ?? config?.LogLevel ?? LogLevel.Warning;

			IsOfflineEnabled = !isSandboxMode && (IsOfflineEnabled
				|| (config != null && config.LogMode == LogMode.File)
				|| (config != null && config.LogMode == LogMode.Both));

			if (IsOfflineEnabled)
			{
				IsOfflineOnly = IsOfflineOnly || (config != null && config.LogMode == LogMode.File);
				InitOfflineLog(config == null ? "" : config.LogPath, false, config);
			}

			try
			{
				if (UserId == Guid.Empty && !IsOfflineOnly)
				{
					UserId = ((WhoAmIResponse) OrganizationService.Execute(new WhoAmIRequest())).UserId;
				}
			}
			catch
			{
				// ignored
			}

			InitLogEntity(callingFunction);
		}

		/// <summary>
		///     Initializes the parent log entity.
		/// </summary>
		private void InitLogEntity([CallerMemberName] string callingFunction = "")
		{
			parentLog =
				new Entity("ldv_log")
				{
					Id = Guid.NewGuid(),
					["ldv_name"] = $"Log-{LogStartDate.ToLocalTime():yyyy_MM_dd-HH_mm_ss_fff}",
					["ldv_assembly"] = GetType().Assembly.FullName,
					["ldv_entryclass"] = Helpers.GetClassName(-1, "CrmLog"),
					["ldv_entryfunction"] = callingFunction,
					["ldv_startdate"] = LogStartDate,
					["ldv_user"] = new EntityReference("systemuser", UserId),
					["ldv_executionendstate"] = new OptionSetValue((int) ExecutionEndState.Success)
				};
		}

		/// <summary>
		///     Sets the emulated 'regarding' fields in the parent log
		/// </summary>
		/// <param name="regardingType">The logical name of the concerned entity.</param>
		/// <param name="regardingId">The ID of the regarding record.</param>
		/// <param name="regardingName">
		///     [OPTIONAL] The primary field value of the concerned entity (usually the 'xxx_name' field
		///     value).
		/// </param>
		public void SetRegarding(string regardingType, string regardingId, string regardingName = "")
		{
			parentLog["ldv_regardingtype"] = regardingType;
			parentLog["ldv_regardingid"] = regardingId;
			parentLog["ldv_regardingname"] = regardingName;
		}

		/// <summary>
		///     Sets the emulated 'regarding' fields in the parent log
		/// </summary>
		/// <param name="regardingType">The logical name of the concerned entity.</param>
		/// <param name="regardingId">The ID of the regarding record.</param>
		/// <param name="regardingName">
		///     [OPTIONAL] The primary field value of the concerned entity (usually the 'xxx_name' field
		///     value).
		/// </param>
		public void SetRegarding(string regardingType, Guid regardingId, string regardingName = "")
		{
			SetRegarding(regardingType, regardingId.ToString(), regardingName);
		}

		/// <summary>
		///     Sets the emulated 'regarding' fields in the parent log
		/// </summary>
		/// <param name="regarding">The reference to the concerned entity.</param>
		/// <param name="regardingName">
		///     [OPTIONAL] The primary field value of the concerned entity (usually the 'xxx_name' field
		///     value).
		/// </param>
		public void SetRegarding(EntityReference regarding, string regardingName = "")
		{
			SetRegarding(regarding.LogicalName, regarding.Id, regardingName);
		}

		/// <summary>
		///     Sets the emulated 'regarding' fields in the parent log.
		/// </summary>
		/// <param name="regarding">The concerned entity.</param>
		/// <param name="primaryFieldName">
		///     [OPTIONAL=null]If null, fetch the 'name' field if it does not have a value in regarding
		///     passed.
		/// </param>
		public void SetRegarding(Entity regarding, string primaryFieldName = null)
		{
			try
			{
				var nameField = string.IsNullOrEmpty(primaryFieldName) && MaxLogLevel == LogLevel.Debug
					? CrmHelpers.GetEntityAttribute<string>(OrganizationService, regarding.LogicalName,
						CrmHelpers.EntityAttribute.PrimaryNameAttribute)
					: primaryFieldName;
				var name = regarding.GetAttributeValue<string>(nameField)
					?? (MaxLogLevel == LogLevel.Debug
						? CrmHelpers.GetRecordName(OrganizationService, regarding.LogicalName, regarding.Id, nameField)
						: "");
				SetRegarding(regarding.ToEntityReference(), name);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Sets the title field in the parent log.
		/// </summary>
		/// <param name="title">Title to set.</param>
		public void SetTitle(string title)
		{
			parentLog["ldv_title"] = title;
		}

		/// <summary>
		///     Sets the title field in the parent log in the format 'Log for "{name}".'.
		/// </summary>
		/// <param name="name">Name of the record.</param>
		public void SetDefaultTitle(string name)
		{
			SetTitle("Log for \"" + name + "\".");
		}

		/// <summary>
		///     Sets the title field in the parent log. Also, sets the regarding with the primary name.<br />
		///     Pass a format with '{name}' to be replaced by record name.
		/// </summary>
		/// <param name="regarding">The concerned entity.</param>
		/// <param name="primaryFieldName">
		///     [OPTIONAL=null]If null, fetch the 'name' field if it does not have a value in regarding
		///     passed.
		/// </param>
		/// <param name="titleTemplate">[OPTIONAL=null]If null, uses 'Log for "{name}".'.</param>
		public void SetTitle(Entity regarding, string primaryFieldName = null, string titleTemplate = null)
		{
			SetRegarding(regarding, primaryFieldName);

			var name = parentLog.GetAttributeValue<string>("ldv_regardingname");

			if (string.IsNullOrEmpty(name))
			{
				return;
			}

			var title = (string.IsNullOrEmpty(titleTemplate) ? "Log for \"{name}\"." : titleTemplate)
				.Replace("{name}", name);
			parentLog["ldv_title"] = title;
		}

		/// <summary>
		///     Sets the entry class of the parent log.
		/// </summary>
		public void SetEntryClass(string entryClass = null)
		{
			parentLog["ldv_entryclass"] = entryClass ?? Helpers.GetClassName(-1, "CrmLog");
		}

		/// <summary>
		///     Initializes the offline log.
		/// </summary>
		/// <param name="logPath">
		///     [OPTIONAL] The file to log to. Should be absolute path in the file system.
		///     Logs to C:\Logs\[assemblyName].csv by default.
		/// </param>
		/// <param name="isFailOver">[OPTIONAL] If 'true', only log to file if CRM is unavailable.</param>
		/// <param name="config">[OPTIONAL] Parameters to use in managing offline logging.</param>
		public void InitOfflineLog(string logPath = "", bool isFailOver = true, FileConfiguration config = null)
		{
			Config = config;
			OfflinePath = logPath;
			IsFailOver = isFailOver;
			IsOfflineEnabled = true;
		}

		/// <summary>
		///     Initializes console logging.
		/// </summary>
		/// <param name="isAutoLog">[OPTIONAL] If 'true', automatically output log entries to the console. Default is 'true'.</param>
		/// <param name="maxLogLevel">[OPTIONAL] The maximum log level to output to console. Default is 'Info'.</param>
		public void InitConsoleLog(bool isAutoLog = true, LogLevel maxLogLevel = LogLevel.Info)
		{
			MaxConsoleLogLevel = maxLogLevel;
			IsConsoleEnabled = true;
			IsAutoLogToConsole = isAutoLog;
		}

		#endregion

		#region Function start/end logging

		/// <summary>
		///     Logs the start of execution.
		/// </summary>
		/// <param name="message">The message to set in the log entry</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogExecutionStart(string message, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogExecutionStart(new LogEntry(message), context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the start of execution.
		/// </summary>
		/// <param name="logEntry">[Optional] The log entry object that includes all relevant information</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogExecutionStart(LogEntry logEntry = null, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				ExecutionStarted = true;

				var defaultMessage = "Started execution: " + callingFunction;

				logEntry = logEntry ?? new LogEntry(defaultMessage);

				// log this at any level
				logEntry.Level = LogLevel.None;

				// set as root in call sequence
				execSeqStack.Push(logEntry);

				if (string.IsNullOrEmpty(logEntry.Message))
				{
					logEntry.Message = defaultMessage;
				}

				Log(logEntry, "", "", context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), -1,
					true, callingFunction, callingLineNumber);

				// start measuring execution duration
				executionTimer = Stopwatch.StartNew();
				// push a zero-based timestamp
				durationsStack.Push(0);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the start of a function.
		/// </summary>
		/// <param name="message">The message to set in the log entry</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogFunctionStart(string message, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogFunctionStart(new LogEntry(message), context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the start of a function.
		/// </summary>
		/// <param name="logEntry">[Optional] The log entry object that includes all relevant information</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogFunctionStart(LogEntry logEntry = null, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				if (!ExecutionStarted)
				{
					LogExecutionStart(callingClass: callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
						stackTrace: stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"),
						callingFunction: callingFunction, callingLineNumber: callingLineNumber);
				}

				var defaultMessage = "Started: " + callingFunction;

				logEntry = logEntry ?? new LogEntry(defaultMessage);

				// set the parent as the previous in the stack, and set as parent in call sequence for next entries
				logEntry.ParentLogEntry = execSeqStack.Peek();
				execSeqStack.Push(logEntry);

				if (string.IsNullOrEmpty(logEntry.Message))
				{
					logEntry.Message = defaultMessage;
				}

				logEntry.Level = LogLevel.Debug;

				Log(logEntry, "", "", context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), -1,
					true, callingFunction, callingLineNumber);

				// start measuring function duration
				functionTimersStack.Push(Stopwatch.StartNew());
				executionTimer = executionTimer ?? functionTimersStack.Peek(); // keep the first timer to log execution time
				// push a zero-based timestamp
				durationsStack.Push(0);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the end of a function.
		/// </summary>
		/// <param name="message">The message to set in the log entry</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogFunctionEnd(string message, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogFunctionEnd(new LogEntry(message), context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the end of a function.
		/// </summary>
		/// <param name="logEntry">[Optional] The log entry object that includes all relevant information</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogFunctionEnd(LogEntry logEntry = null, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				// stop function timer and get the elapsed time
				functionTimersStack.Peek().Stop();
				var elapsedTime = (int) functionTimersStack.Pop().ElapsedMilliseconds;
				// remove the duration reference of the function
				durationsStack.Pop();

				var defaultMessage = "Finished: " + callingFunction;

				logEntry = logEntry ?? new LogEntry(defaultMessage);

				// log this at any level
				logEntry.Level = LogLevel.None;

				// set parent, then remove it from sequence of calls
				execSeqStack.Pop();
				if (execSeqStack.Any())
				{
					logEntry.ParentLogEntry = execSeqStack.Peek();
				}

				if (string.IsNullOrEmpty(logEntry.Message))
				{
					logEntry.Message = defaultMessage;
				}

				logEntry.Level = LogLevel.Debug;

				Log(logEntry, "", "", context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), elapsedTime,
					true, callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the end of execution.
		/// </summary>
		/// <param name="message">The message to set in the log entry</param>
		/// <param name="autoFlush">[Optional] Automatically flush after ending exectution</param>
		/// <param name="state">[Optional] The state at which the execution ended</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogExecutionEnd(string message, bool autoFlush = true, ExecutionEndState state = ExecutionEndState.Success,
			IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogExecutionEnd(autoFlush, state, new LogEntry(message), context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), callingFunction, callingLineNumber);
			}
			catch (FlushException)
			{
				throw;
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the end of execution.
		/// </summary>
		/// <param name="state">[Optional] The state at which the execution ended</param>
		/// <param name="message">[Optional] The message to set in the log entry</param>
		/// <param name="autoFlush">[Optional] Automatically flush after ending exectution</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogExecutionEnd(ExecutionEndState state, string message = "", bool autoFlush = true,
			IExecutionContext context = null, string callingClass = null, string stackTrace = null,
			[CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogExecutionEnd(autoFlush, state, new LogEntry(message), context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), callingFunction, callingLineNumber);
			}
			catch (FlushException)
			{
				throw;
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the end of execution.
		/// </summary>
		/// <param name="autoFlush">[Optional] Automatically flush after ending exectution</param>
		/// <param name="state">[Optional] The state at which the execution ended</param>
		/// <param name="logEntry">[Optional] The log entry object that includes all relevant information</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogExecutionEnd(bool autoFlush = true, ExecutionEndState state = ExecutionEndState.Success,
			LogEntry logEntry = null,
			IExecutionContext context = null, string callingClass = null, string stackTrace = null,
			[CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				// stop execution timer
				executionTimer.Stop();
				// remove the duration reference of the function
				durationsStack.Pop();

				AssemblyExecutionState = (AssemblyExecutionState == ExecutionEndState.Failure) ? AssemblyExecutionState : state;

				var defaultMessage = "Finished execution: " + callingFunction;

				logEntry = logEntry ?? new LogEntry(defaultMessage);

				// log this at any level
				logEntry.Level = LogLevel.None;

				// set parent, then remove it from sequence of calls
				if (execSeqStack.Any())
				{
					logEntry.ParentLogEntry = execSeqStack.Pop();
				}

				if (string.IsNullOrEmpty(logEntry.Message))
				{
					logEntry.Message = defaultMessage;
				}

				Log(logEntry, "", "", context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"),
					(int) executionTimer.ElapsedMilliseconds, true, callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
			finally
			{
				if (autoFlush)
				{
					Flush();
				}
			}
		}

		#endregion

		#region Logging

		/// <summary>
		///     Logs an entry.
		/// </summary>
		/// <param name="message">The message to set in the log entry</param>
		/// <param name="level">[Optional] The logging level, above which, this entry will be skipped</param>
		/// <param name="regardingType">[Optional] The logical name of the entity concerned with this log entry as a string</param>
		/// <param name="regardingName">[Optional] The name of the record concerned with this log entry as a string</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void Log(string message, LogLevel level = LogLevel.Info, string regardingType = "",
			string regardingName = "", IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				Log(new LogEntry(message, level), regardingType, regardingName, context,
					callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), null, false,
					callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs an exception.
		/// </summary>
		/// <param name="exception">The exception that was thrown</param>
		/// <param name="regardingType">[Optional] The logical name of the entity concerned with this log entry as a string</param>
		/// <param name="regardingName">[Optional] The name of the record concerned with this log entry as a string</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void Log(Exception exception, IExecutionContext context, string regardingType = "",
			string regardingName = "", string callingClass = null, string stackTrace = null,
			[CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				Log(new LogEntry(exception), regardingType, regardingName, context,
					callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? exception.StackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), null, false,
					callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs an exception.
		/// </summary>
		/// <param name="exception">The exception that was thrown</param>
		/// <param name="regardingType">[Optional] The logical name of the entity concerned with this log entry as a string</param>
		/// <param name="regardingName">[Optional] The name of the record concerned with this log entry as a string</param>
		/// <param name="information">
		///     [Optional] Extra information to add to the log entry (in this case, it should be the context
		///     parse)
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void Log(Exception exception, string regardingType = "", string regardingName = "", string information = "",
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				Log(new LogEntry(exception, information: information), regardingType, regardingName,
					callingClass: callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace: stackTrace ?? exception.StackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"),
					callingFunction: callingFunction, callingLineNumber: callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs an entry.
		/// </summary>
		/// <param name="logEntry">The log entry object that includes all relevant information</param>
		/// <param name="regardingType">[Optional] The logical name of the entity concerned with this log entry as a string</param>
		/// <param name="regardingName">[Optional] The name of the record concerned with this log entry as a string</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="elapsedTime">[Non-user]</param>
		/// <param name="parent">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void Log(LogEntry logEntry, string regardingType = "", string regardingName = "",
			IExecutionContext context = null, string callingClass = null, string stackTrace = null, int? elapsedTime = null,
			bool parent = false, [CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				if (!ExecutionStarted)
				{
					LogExecutionStart(callingClass: callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
						stackTrace: stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"),
						callingFunction: callingFunction, callingLineNumber: callingLineNumber);
				}

				// set parent of this entry to top of the stack entry
				if (!parent)
				{
					logEntry.ParentLogEntry = execSeqStack.Peek();
				}

				// calculate the duration between the last log entry in this function and this one
				if (elapsedTime == null)
				{
					// get the last recorded duration since function start
					var lastDuration = durationsStack.Pop();
					// get the function timer
					var functionDuration
						= (int) (functionTimersStack.Any()
							? functionTimersStack.Peek().ElapsedMilliseconds
							: executionTimer.ElapsedMilliseconds);
					// add a snapshot
					durationsStack.Push(functionDuration);

					// calculate the time it took to get from the last recorded entry to this entry
					logEntry.ElapsedTime = functionDuration - lastDuration;
				}
				else
				{
					logEntry.ElapsedTime = elapsedTime.Value;
				}

				logEntry.StartDate = DateTime.UtcNow.AddMilliseconds(-logEntry.ElapsedTime);

				logEntry.RegardingType = regardingType ?? logEntry.RegardingType;
				logEntry.RegardingName = regardingName ?? logEntry.RegardingName;

				// exception flag
				if (logEntry.Exception != null)
				{
					exceptionThrown = true;
					logEntry.ExceptionThrown = true;
				}

				if (logEntry.ExceptionThrown)
				{
					var logEntryTemp = logEntry;

					while ((logEntryTemp = logEntryTemp.ParentLogEntry) != null)
					{
						logEntryTemp.ExceptionThrown = true;
					}
				}

				// plugin context
				if (context != null)
				{
					logEntry.Information = PluginInfo.GetPluginExecutionInfo(OrganizationService, context);
				}

				// increment the log entries index
				logEntry.CurrentEntryIndex = CurrentEntryIndex++;

				// code info
				logEntry.StackTrace = stackTrace ?? logEntry.StackTrace ?? Helpers.GetStackTrace(-1, "CrmLog");
				logEntry.CallingClass = callingClass ?? logEntry.CallingClass ?? Helpers.GetClassName(-1, "CrmLog");
				logEntry.CallingFunction = callingFunction ?? logEntry.CallingFunction;
				logEntry.CallingLineNumber = callingLineNumber;

				logQueue.Enqueue(logEntry);

				// keep the first entry with an exception to show in root
				exceptionLogEntry = logEntry.ExceptionThrown ? logEntry : exceptionLogEntry;

				if (IsAutoLogToConsole)
				{
					LogToConsole(logEntry);
				}
			}
			catch
			{
				// ignored
			}
		}

		public void LogKeyValues(string title, string[] paramNames, object[] paramValues,
			LogLevel level = LogLevel.Info, string regardingType = "",
			string regardingName = "", IExecutionContext context = null,
			[CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				var paramInfoBuilder = new StringBuilder();

				for (var i = 0; i < paramNames.Length; i++)
				{
					if (i != 0)
					{
						paramInfoBuilder.Append("\r\n");
					}

					paramInfoBuilder
						.Append(paramNames[i])
						.Append(" = ")
						.Append(Helpers.GetStringRepresentation(paramValues[i]));
				}

				Log(new LogEntry(title, level, null, null, paramInfoBuilder.ToString()),
					regardingType, regardingName, context,
					Helpers.GetClassName(-1, "CrmLog"), Helpers.GetStackTrace(-1, "CrmLog"),
					null, false, callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		public void LogKeyValues(string title, IDictionary<string, object> parameters,
			LogLevel level = LogLevel.Info, string regardingType = "",
			string regardingName = "", IExecutionContext context = null,
			[CallerMemberName] string callingFunction = "", [CallerLineNumber] int callingLineNumber = 0)
		{
			try
			{
				LogKeyValues(title, parameters.Keys.ToArray(), parameters.Values.ToArray(),
					level, regardingType, regardingName, context, callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Logs the current execution line in the code.
		/// </summary>
		/// <param name="message">[OPTIONAL] The message to set in the log entry</param>
		/// <param name="context">
		///     [Optional] The plugin context to parse. If given, the logger will go over all the parameters and
		///     fetch info from CRM
		/// </param>
		/// <param name="callingClass">[Non-user]</param>
		/// <param name="stackTrace">[Non-user]</param>
		/// <param name="callingFunction">[Non-user]</param>
		/// <param name="callingLineNumber">[Non-user]</param>
		public void LogLine(string message = null, IExecutionContext context = null,
			string callingClass = null, string stackTrace = null, [CallerMemberName] string callingFunction = "",
			[CallerLineNumber] int callingLineNumber = 0)
		{
			if (MaxLogLevel == LogLevel.None)
			{
				return;
			}

			try
			{
				if (!ExecutionStarted)
				{
					LogExecutionStart(callingClass: callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
						stackTrace: stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"),
						callingFunction: callingFunction, callingLineNumber: callingLineNumber);
				}

				var defaultMessage = "Execution reached line " + callingLineNumber;

				var logEntry = new LogEntry(message ?? defaultMessage);
				logEntry.Level = LogLevel.Debug;

				Log(logEntry, "", "", context, callingClass ?? Helpers.GetClassName(-1, "CrmLog"),
					stackTrace ?? Helpers.GetStackTrace(-1, "CrmLog"), -1,
					false, callingFunction, callingLineNumber);
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Formats the message with colouring and timestamp, and outputs to console if available.
		/// </summary>
		/// <param name="message">The message to output to console.</param>
		/// <param name="logLevel">Log level to use for formatting. Default is 'Info'.</param>
		public void LogToConsole(string message, LogLevel logLevel = LogLevel.Info)
		{
			LogToConsole(new LogEntry(message, logLevel));
		}

		/// <summary>
		///     Formats the log entry with colouring and timestamp, and outputs to console if available.
		/// </summary>
		/// <param name="logEntry">The log entry object that includes all relevant information.</param>
		public void LogToConsole(LogEntry logEntry)
		{
			try
			{
				if (!IsConsoleEnabled || logEntry.Level > MaxConsoleLogLevel)
				{
					return;
				}

				if (logEntry.ExceptionThrown)
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("============================ !! EXCEPTION !! ===============================");
					Console.ResetColor();
					Console.WriteLine(GetFormattedEntry(logEntry));
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("============================================================================");
					Console.ResetColor();
				}
				else
				{
					Console.ForegroundColor = ConsoleColor.DarkGreen;
					Console.Write("{0:ddd hh:mm:ss tt: }", DateTime.Now);

					switch (logEntry.Level)
					{
						case LogLevel.Error:
							Console.BackgroundColor = ConsoleColor.White;
							Console.ForegroundColor = ConsoleColor.Red;
							break;
						case LogLevel.Warning:
							Console.ForegroundColor = ConsoleColor.Yellow;
							break;
						case LogLevel.Debug:
							Console.ForegroundColor = ConsoleColor.DarkCyan;
							break;
						default:
							Console.ResetColor();
							break;
					}

					Console.WriteLine(logEntry.Message);
					Console.ResetColor();
				}
			}
			catch (Exception)
			{
				// ignored
			}
		}

		/// <summary>
		///     Sets the state at which the execution ended
		/// </summary>
		/// <param name="state">The state at which the execution ended</param>
		public void SetExecutionState(ExecutionEndState state)
		{
			AssemblyExecutionState = state;
		}

		/// <summary>
		///     Sets the state at which the execution ended as a failure
		/// </summary>
		public void ExecutionFailed()
		{
			AssemblyExecutionState = ExecutionEndState.Failure;
		}

		#endregion

		#region Flush

		/// <summary>
		///     Created the parent log, and creates each log entry in the queue.
		/// </summary>
		/// <param name="inBulk">If true, created all log entries using ExecuteMultiple</param>
		public void Flush(bool inBulk = true)
		{
			var lockObject = this;

			Action action =
				() =>
				{
					lock (lockObject)
					{
						if (MaxLogLevel == LogLevel.None
							|| logQueue.All(entry => entry.Level == LogLevel.None)
							|| (logQueue.All(entry => entry.Level == LogLevel.None || entry.Level > MaxLogLevel)
								&& AssemblyExecutionState != ExecutionEndState.Failure))
						{
							return;
						}

						try
						{
							parentLog["ldv_executionendstate"] = new OptionSetValue((int) AssemblyExecutionState);
							parentLog["ldv_executionduration"] = (int) executionTimer.ElapsedMilliseconds;
							parentLog["ldv_exceptionthrown"] = exceptionThrown;

							if (IsOfflineEnabled)
							{
								offlineQueue = new Queue<LogEntry>(logQueue);
							}

							if (IsOfflineOnly || isBulkPreviously == true)
							{
								logQueue.Clear();
								return;
							}

							var requests = new List<OrganizationRequest>();
							var serialisedLogEntries = new StringBuilder();

							inBulk = isBulkPreviously == null && inBulk;

							if (!inBulk && !isParentCreated)
							{
								OrganizationService.Create(parentLog);
							}

							while (logQueue.Count > 0)
							{
								var logEntry = logQueue.Dequeue();

								// if the log entry's level is above the maximum, then no need to log it
								// unless the execution failed
								if (logEntry.Level > MaxLogLevel
									&& AssemblyExecutionState != ExecutionEndState.Failure)
								{
									continue;
								}

								var target = CreateEntryEntity(logEntry);

								if (inBulk)
								{
									serialisedLogEntries.Append("<|||>")
										.Append(EntitySerializer.SerializeObject(target));
								}
								else
								{
									OrganizationService.Create(target);
								}
							}

							if (exceptionLogEntry != null)
							{
								var updatedParent =
									new Entity("ldv_log")
									{
										Id = parentLog.Id,
										["ldv_exceptionlogentry"] =
											new EntityReference("ldv_logentry", exceptionLogEntry.LogEntryId),
										["ldv_exceptionmessage"] = exceptionLogEntry.Message
									};

								if (inBulk)
								{
									serialisedLogEntries.Append("<|||><PARENT>")
										.Append(EntitySerializer.SerializeObject(updatedParent));
								}
								else
								{
									OrganizationService.Update(updatedParent);
								}
							}

							if (inBulk)
							{
								parentLog["ldv_serialisedlogentries"] = serialisedLogEntries.ToString().Compress();
								requests.Add(new CreateRequest {Target = parentLog});
								CrmHelpers.ExecuteBulk(OrganizationService, requests);
							}

							isParentCreated = true;
							isBulkPreviously = inBulk;
						}
						catch (Exception ex)
						{
							try
							{
								if (IsFailOver)
								{
									FlushOffline();
								}
							}
							catch
							{ }

							throw new FlushException(ex.Message, ex);
						}
						finally
						{
							if (!IsFailOver)
							{
								FlushOffline();
							}
						}
					}
				};

			if (isPlugin || IsOfflineEnabled)
			{
				action.Invoke();
			}
			else
			{
				new Thread(() => action()).Start();
			}
		}

		/// <summary>
		///     Flushes to local file.
		/// </summary>
		private void FlushOffline()
		{
			try
			{
				if (!IsOfflineEnabled)
				{
					return;
				}

				// no file name specified, create default name
				if (string.IsNullOrEmpty(OfflinePath))
				{
					OfflinePath = $"C:\\Logs\\{GetType().Assembly.GetName().Name}.csv";
				}

				// if directory was given, add filename to it
				if (!Path.HasExtension(OfflinePath))
				{
					OfflinePath = Path.Combine(OfflinePath, GetType().Assembly.GetName().Name + ".csv");
				}

				// if categorisation is needed, add the assembly folder
				if (Config != null && Config.CategoriseByType == true)
				{
					OfflinePath = Path.Combine(Path.GetDirectoryName(OfflinePath), GetType().Assembly.GetName().Name,
						Path.GetFileName(OfflinePath));
				}

				// create path if it doesn't exist
				Directory.CreateDirectory(Path.GetDirectoryName(OfflinePath));

				var isCreateFile = false;

				// if the file already and config exists, check size and date limit for split
				if (File.Exists(OfflinePath) && Config != null)
				{
					var fileDate = new FileInfo(OfflinePath).CreationTimeUtc;
					var extension = Path.GetExtension(OfflinePath);
					var datedName = OfflinePath.Replace(extension,
						string.Format("_{0:" + (Config.FileDateFormat ?? "yyyy-MM-dd_HH-mm-ss-fff") + "}{1}",
							fileDate.ToLocalTime(), extension));

					// option exists for size limit
					if ((Config.FileSplitMode == SplitMode.Size || Config.FileSplitMode == SplitMode.Both)
						&& Config.MaxFileSize > 0 && new FileInfo(OfflinePath).Length / 1024 > Config.MaxFileSize)
					{
						File.Move(OfflinePath, datedName);
						isCreateFile = true;
					}
					else if ((Config.FileSplitMode == SplitMode.Date || Config.FileSplitMode == SplitMode.Both)
						&& Config.FileSplitFrequency != null) // option exists for date limit
					{
						var splitDate = Config.FileSplitDate;

						// calculate the target split date for the file based on its creation date
						switch (Config.FileSplitFrequency)
						{
							case SplitFrequency.Hourly:
								splitDate = new DateTime(fileDate.Year, fileDate.Month, fileDate.Day,
									fileDate.Hour, splitDate.Minute, splitDate.Second).AddHours(1);
								break;

							case SplitFrequency.Daily:
								splitDate = new DateTime(fileDate.Year, fileDate.Month, fileDate.Day,
									splitDate.Hour, splitDate.Minute, splitDate.Second).AddDays(1);
								break;

							case SplitFrequency.Monthly:
								splitDate = new DateTime(fileDate.Year, fileDate.Month, splitDate.Day,
									splitDate.Hour, splitDate.Minute, splitDate.Second).AddMonths(1);
								break;

							case SplitFrequency.Yearly:
								splitDate = new DateTime(fileDate.Year, splitDate.Month, splitDate.Day,
									splitDate.Hour, splitDate.Minute, splitDate.Second).AddYears(1);
								break;
						}

						// rename
						if (DateTime.UtcNow > splitDate)
						{
							File.Move(OfflinePath, datedName);
							isCreateFile = true;
						}
					}
				}
				else
				{
					isCreateFile = true;
				}

				if (isCreateFile)
				{
					CreateLogFile();
				}

				// open the log file and write the log entries
				using (var stream =
					new StreamWriter(File.Open(OfflinePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
				{
					while (offlineQueue.Any())
					{
						stream.Write(GetFormattedCsvEntry(offlineQueue.Dequeue()));
					}

					stream.Flush();
				}
			}
			catch
			{
				// ignored
			}
		}

		/// <summary>
		///     Creates an entity containing the log entry data
		/// </summary>
		/// <param name="logEntry">The log entry.</param>
		/// <returns>The CRM entity object including all info in the log entry object</returns>
		private Entity CreateEntryEntity(LogEntry logEntry)
		{
			var target = new Entity("ldv_logentry")
						 {
							 Id = logEntry.LogEntryId,
							 ["ldv_name"] = BuildLogId(logEntry),
							 ["ldv_message"] = logEntry.Message,
							 ["ldv_entryindex"] = logEntry.CurrentEntryIndex,
							 ["ldv_startdate"] = logEntry.StartDate,
							 ["ldv_regardingtype"] = logEntry.RegardingType,
							 ["ldv_regardingname"] = logEntry.RegardingName,
							 ["ldv_stacktrace"] = logEntry.StackTrace
						 };

			if (logEntry.Exception != null)
			{
				if (string.IsNullOrEmpty(logEntry.Message))
				{
					target["ldv_message"] = logEntry.Message = "Exception: " + logEntry.Exception.Message;
				}

				target["ldv_exception"] = logEntry.Exception.GetType().Name;
				target["ldv_exceptionsource"] = logEntry.Exception.Source;
				target["ldv_stacktrace"] = logEntry.Exception.StackTrace;

				if (logEntry.Exception.InnerException != null)
				{
					target["ldv_innerexception"] = logEntry.Exception.InnerException.GetType().Name;
					target["ldv_innerexceptionmessage"] = logEntry.Exception.InnerException.Message;
					target["ldv_innerexceptionsource"] = logEntry.Exception.InnerException.Source;
					target["ldv_innerexceptionstacktrace"] = logEntry.Exception.InnerException.StackTrace;
				}
			}

			target["ldv_exceptionthrown"] = logEntry.ExceptionThrown;

			target["ldv_level"] = new OptionSetValue((int) logEntry.Level);

			if (logEntry.UserId != Guid.Empty)
			{
				target["ldv_user"] = new EntityReference("systemuser", logEntry.UserId);
			}
			else if (UserId != Guid.Empty)
			{
				target["ldv_user"] = new EntityReference("systemuser", UserId);
			}

			target["ldv_assembly"] = logEntry.Assembly ?? GetType().Assembly.FullName;
			target["ldv_class"] = logEntry.CallingClass;
			target["ldv_callingfunction"] = logEntry.CallingFunction;
			target["ldv_linenumber"] = logEntry.CallingLineNumber;

			if (logEntry.ElapsedTime > -1)
			{
				target["ldv_executionduration"] = logEntry.ElapsedTime;
			}

			target["ldv_information"] = logEntry.Information;

			target["ldv_parentlogid"] = parentLog.ToEntityReference();

			if (logEntry.ParentLogEntry != null)
			{
				target["ldv_parentlogentryid"] = new EntityReference("ldv_logentry", logEntry.ParentLogEntry.LogEntryId);
			}

			return target;
		}

		private static string BuildLogId(LogEntry logEntry)
		{
			return string.Format("LogEntry-{0:yyyy_MM_dd-HH_mm_ss_fff}",
				logEntry.StartDate.HasValue ? logEntry.StartDate.Value.ToLocalTime() : DateTime.Now);
		}

		private void CreateLogFile()
		{
			File.Create(OfflinePath).Close();

			var data = Encoding.UTF8.GetBytes("Log ID,Assembly,Entry Class,Entry Function,Date,Index," +
				"Log Level,Duration (ms),Class,Function,Line Number,Message,Exception Thrown," +
				"Exception,Source,Stack Trace,Inner Exception,Inner Source,Information," +
				"Log Start Date,Execution State,Execution Duration," +
				"Regarding Type,Regarding Name,Regarding ID,User ID\r\n");
			var bytes = Encoding.UTF8.GetPreamble().Concat(data).ToArray();
			File.WriteAllBytes(OfflinePath, bytes);
		}

		private string GetFormattedEntry(LogEntry logEntry)
		{
			var stringBuilder = new StringBuilder();

			stringBuilder.AppendFormat("Start date: {0}\r\n", logEntry.StartDate.GetValueOrDefault().ToLocalTime());
			stringBuilder.AppendFormat("Log level: {0}\r\n", logEntry.Level);
			stringBuilder.AppendFormat("Duration: {0} ms\r\n", logEntry.ElapsedTime);
			stringBuilder.AppendFormat("Assembly: {0}\r\n", logEntry.Assembly ?? GetType().Assembly.FullName);
			stringBuilder.AppendFormat("Class: {0}\r\n", logEntry.CallingClass);
			stringBuilder.AppendFormat("Calling function: {0}\r\n", logEntry.CallingFunction);
			stringBuilder.AppendFormat("Line number: {0}\r\n", logEntry.CallingLineNumber);

			if (logEntry.Exception != null)
			{
				if (string.IsNullOrEmpty(logEntry.Message))
				{
					stringBuilder.AppendFormat("Message: {0}\r\n", logEntry.Exception.Message);
				}

				stringBuilder.AppendFormat("Exception: {0}\r\n" + "Source: {1}\r\n" + "Stack trace: {2}\r\n",
					logEntry.Exception.GetType().Name, logEntry.Exception.Source, logEntry.Exception.StackTrace);

				if (logEntry.Exception.InnerException != null)
				{
					stringBuilder.AppendFormat("Inner exception: {0}\r\n" + "Source: {1}\r\n",
						logEntry.Exception.InnerException.GetType().Name, logEntry.Exception.InnerException.Message);
				}
			}
			else
			{
				stringBuilder.AppendFormat("Message: {0}\r\n", logEntry.Message);
				stringBuilder.AppendFormat("Stack trace: {0}\r\n", logEntry.StackTrace);
			}

			stringBuilder.AppendFormat("Information: {0}", logEntry.Information);

			return stringBuilder.ToString();
		}

		private string GetFormattedCsvEntry(LogEntry logEntry)
		{
			var stringBuilder = new StringBuilder();


			stringBuilder.AppendFormat("{0}", parentLog.GetAttributeValue<string>("ldv_name"));
			stringBuilder.AppendFormat(",{0}", EscapeCsv(logEntry.Assembly ?? GetType().Assembly.FullName));
			stringBuilder.AppendFormat(",{0}", parentLog.GetAttributeValue<string>("ldv_entryclass"));
			stringBuilder.AppendFormat(",{0}", parentLog.GetAttributeValue<string>("ldv_entryfunction"));
			stringBuilder.AppendFormat(",{0}", logEntry.StartDate.GetValueOrDefault().ToLocalTime());
			stringBuilder.AppendFormat(",{0}", logEntry.CurrentEntryIndex);
			stringBuilder.AppendFormat(",{0}", logEntry.Level);
			stringBuilder.AppendFormat(",{0}", EscapeCsv(logEntry.ElapsedTime.ToString()));
			stringBuilder.AppendFormat(",{0}", logEntry.CallingClass);
			stringBuilder.AppendFormat(",{0}", logEntry.CallingFunction);
			stringBuilder.AppendFormat(",{0}", logEntry.CallingLineNumber);

			if (logEntry.Exception != null)
			{
				if (string.IsNullOrEmpty(logEntry.Message))
				{
					stringBuilder.AppendFormat(",{0}", EscapeCsv(logEntry.Exception.Message));
				}

				stringBuilder.AppendFormat(",{0}", logEntry.ExceptionThrown);
				stringBuilder.AppendFormat(",{0},{1},{2}",
					logEntry.Exception.GetType().Name,
					EscapeCsv(logEntry.Exception.Source), EscapeCsv(logEntry.Exception.StackTrace));

				if (logEntry.Exception.InnerException != null)
				{
					stringBuilder.AppendFormat(",{0},{1}",
						logEntry.Exception.InnerException.GetType().Name,
						EscapeCsv(logEntry.Exception.InnerException.Message));
				}
				else
				{
					stringBuilder.Append(",,");
				}
			}
			else
			{
				stringBuilder.AppendFormat(",{0},{1},,,,,", EscapeCsv(logEntry.Message), logEntry.ExceptionThrown);
			}

			stringBuilder.AppendFormat(",{0}", EscapeCsv(logEntry.Information));

			stringBuilder.AppendFormat(",{0}", LogStartDate);
			stringBuilder.AppendFormat(",{0}", AssemblyExecutionState);
			stringBuilder.AppendFormat(",{0}", parentLog.GetAttributeValue<int>("ldv_executionduration"));
			stringBuilder.AppendFormat(",{0}", logEntry.RegardingType);
			stringBuilder.AppendFormat(",{0}", EscapeCsv(logEntry.RegardingName));
			stringBuilder.AppendFormat(",{0}", parentLog.GetAttributeValue<string>("ldv_regardingid"));
			stringBuilder.AppendFormat(",{0}\r\n", logEntry.UserId == Guid.Empty ? UserId : logEntry.UserId);

			return stringBuilder.ToString();
		}

		private string EscapeCsv(string input)
		{
			if (string.IsNullOrEmpty(input))
			{
				return input;
			}

			return "\"" + input.Replace("\"", "\"\"") + "\"";
		}

		#endregion

		public static void InvalidateCache()
		{
			Helpers.RemoveFromMemCache(CONFIG_MEM_CACHE_KEY);
		}

		#region Inner classes

		public class FlushException : Exception
		{
			public FlushException()
			{ }

			public FlushException(string message) : base(message)
			{ }

			public FlushException(string message, Exception innerException) : base(message, innerException)
			{ }

			protected FlushException(SerializationInfo info, StreamingContext context) : base(info, context)
			{ }
		}

		public class FileConfiguration
		{
			public int? MaxFileSize;
			public string FileDateFormat;
			public SplitMode? FileSplitMode;
			public SplitFrequency? FileSplitFrequency;
			public DateTime FileSplitDate;
			public bool? CategoriseByType;
		}

		private class PrivateConfiguration : FileConfiguration
		{
			internal LogLevel LogLevel;
			internal LogMode LogMode;
			internal string LogPath;
		}

		/// <summary>
		///     Contains information related to the log entry.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public class LogEntry
		{
			#region Instance variables

			public readonly Guid LogEntryId = Guid.NewGuid();

			public LogEntry ParentLogEntry { get; set; }

			public string Message { get; set; }

			public DateTime? StartDate { get; set; }

			public LogLevel Level { get; set; }

			public Guid UserId { get; set; }

			public string RegardingType { get; set; }
			public string RegardingName { get; set; }

			public Exception Exception { get; set; }
			public bool ExceptionThrown { get; set; }

			public string Assembly { get; set; }
			public string StackTrace { get; set; }
			public string CallingClass { get; set; }
			public string CallingFunction { get; set; }
			public int CallingLineNumber { get; set; }

			public string Information { get; set; }

			private int currentEntryIndex;

			public int CurrentEntryIndex
			{
                get { return currentEntryIndex; }
                set { currentEntryIndex = value <= 0 ? 1 : value; }
			}

			private int elapsedTime;

			public int ElapsedTime
			{
                get { return elapsedTime; }
				set { elapsedTime = elapsedTime < -1 ? -1 : value; }
			}

			#endregion

			#region Constructors

			public LogEntry(string message, LogLevel level = LogLevel.Info, string regardingType = "", string regardingName = "",
				string information = "") : this(message, null, level, regardingType, regardingName, information)
			{ }

			public LogEntry(Exception exception, string message = "", string regardingType = "", string regardingName = "",
				string information = "", LogLevel level = LogLevel.Error)
				: this(message, exception, level, regardingType, regardingName, information)
			{ }

			public LogEntry(string message, Exception exception, LogLevel level, string information)
				: this(message, exception, level, "", "", information)
			{ }

			public LogEntry(string message, Exception exception, LogLevel level, string regardingType, string regardingName,
				string information)
			{
				Message = message;
				Level = level;
				Exception = exception;
				RegardingType = regardingType;
				RegardingName = regardingName;
				Information = information;
			}

			public LogEntry(string message, Exception exception, LogLevel level, string regardingType, string regardingName,
				string information, Guid userId) : this(message, exception, level, regardingType, regardingName, information)
			{
				UserId = userId;
			}

			#endregion
		}
	}

	#endregion

	#region Enums

	/// <summary>
	///     The log levels
	/// </summary>
	public enum LogLevel
	{
		None = 0,
		Error = 10,
		Warning = 20,
		Info = 30,
		Debug = 40
	}

	public enum LogMode
	{
		Crm = 10,
		File = 20,
		Both = 30
	}

	public enum SplitMode
	{
		Size = 10,
		Date = 20,
		Both = 30
	}

	public enum SplitFrequency
	{
		Hourly = 10,
		Daily = 20,
		Monthly = 30,
		Yearly = 40
	}

	#endregion

	#endregion

	#region Plugin classes

	public enum PluginUser
	{
		ContextUser,
		InitiatingUser,
		System,
		Custom
	}

	/// <summary>
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	public abstract class PluginLogic<TPlugin> where TPlugin : IPlugin
	{
		protected TPlugin plugin;

		protected IServiceProvider serviceProvider;
		protected IPluginExecutionContext context;
		protected IOrganizationServiceFactory serviceFactory;
		protected ITracingService tracingService;
		protected IOrganizationService service;

		protected CrmLog log;

		public virtual void Execute(TPlugin plugin, IServiceProvider serviceProvider)
		{
			Execute(plugin, serviceProvider, PluginUser.System);
		}

		public virtual void Execute(TPlugin plugin, IServiceProvider serviceProvider, bool isLogEnabled)
		{
			Execute(plugin, serviceProvider, PluginUser.System, null, isLogEnabled);
		}

		public virtual void Execute(TPlugin plugin, IServiceProvider serviceProvider, Guid? userId,
			bool isLogEnabled = true)
		{
			Execute(plugin, serviceProvider, PluginUser.Custom, userId, isLogEnabled);
		}

		public virtual void Execute(TPlugin plugin, IServiceProvider serviceProvider, PluginUser user,
			bool isLogEnabled = true)
		{
			Execute(plugin, serviceProvider, user, null, isLogEnabled);
		}

		public virtual void Execute(TPlugin plugin, IServiceProvider serviceProvider, PluginUser user, Guid? userId,
			bool isLogEnabled = true)
		{
			this.plugin = plugin;
			this.serviceProvider = serviceProvider;

			InitialisePlugin(user, userId);

			try
			{
				tracingService.Trace("Initialising log ...");
				log = new CrmLog(serviceProvider);
				tracingService.Trace("Finished initialising log.");

				if (isLogEnabled)
				{
					tracingService.Trace("Log enabled.");

					tracingService.Trace("Setting entry class in log ...");
					log.SetEntryClass(typeof(TPlugin).FullName);

					tracingService.Trace("Checking regarding to be set in log ...");
					if (context.PrimaryEntityName != null && context.PrimaryEntityName != "none"
						&& context.PrimaryEntityId != Guid.Empty)
					{
						tracingService.Trace("Setting regarding in log ...");
						log.SetRegarding(context.PrimaryEntityName, context.PrimaryEntityId);
					}
				}
			}
			catch (Exception ex)
			{
				throw new InvalidPluginExecutionException("Failed to init log => " + ex.Message, ex);
			}

			try
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Logging execution start ...");
					log.LogExecutionStart();
				}

				tracingService.Trace("Executing plugin logic ...");
				ExecuteLogic();
				tracingService.Trace("Finished executing plugin logic.");
			}
			catch (Exception ex)
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Exception occurred.");
					log.ExecutionFailed();
					tracingService.Trace("Logging exception ...");
					tracingService.Trace(CrmHelpers.BuildExceptionMessage(ex));
					log.Log(ex, context);
				}

				throw new InvalidPluginExecutionException(ex.Message, ex);
			}
			finally
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Logging execution end ...");
					log.LogExecutionEnd();
				}

				tracingService.Trace("Finished executing plugin.");
			}
		}

		protected abstract void ExecuteLogic();

		protected virtual bool IsContextValid()
		{
			return true;
		}

		protected void InitialisePlugin(PluginUser user = PluginUser.System, Guid? userId = null)
		{
			tracingService = (ITracingService) serviceProvider.GetService(typeof(ITracingService));
			tracingService.Trace("Getting context ...");
			context = (IPluginExecutionContext) serviceProvider.GetService(typeof(IPluginExecutionContext));
			tracingService.Trace("Getting factory ...");
			serviceFactory = (IOrganizationServiceFactory) serviceProvider.GetService(typeof(IOrganizationServiceFactory));

			switch (user)
			{
				case PluginUser.ContextUser:
					tracingService.Trace("Running as ContextUser.");
					userId = context.UserId;
					break;

				case PluginUser.InitiatingUser:
					tracingService.Trace("Running as InitiatingUserId.");
					userId = context.InitiatingUserId;
					break;

				case PluginUser.System:
					tracingService.Trace("Running as System.");
					userId = null;
					break;

				case PluginUser.Custom:
					tracingService.Trace($"Running as {userId}.");
					break;

				default:
					throw new ArgumentOutOfRangeException("user", user, "Plugin user type is out of range.");
			}

			tracingService.Trace("Getting service ...");
			service = serviceFactory.CreateOrganizationService(userId);

			if (!IsContextValid())
			{
				throw new InvalidPluginExecutionException("Failed to initialise plugin due to invalid context.");
			}

			tracingService.Trace($"Initialised plugin.");
		}
	}

	/// <summary>
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	public abstract class StepLogic<TCodeActivity> where TCodeActivity : CodeActivity
	{
		protected TCodeActivity codeActivity;

		protected CodeActivityContext executionContext;
		protected IWorkflowContext context;
		protected IOrganizationServiceFactory serviceFactory;
		protected ITracingService tracingService;
		protected IOrganizationService service;

		protected CrmLog log;

		public virtual void Execute(TCodeActivity codeActivity, CodeActivityContext executionContext)
		{
			Execute(codeActivity, executionContext, PluginUser.System);
		}

		public virtual void Execute(TCodeActivity codeActivity, CodeActivityContext executionContext,
			bool isLogEnabled)
		{
			Execute(codeActivity, executionContext, PluginUser.System, null, isLogEnabled);
		}

		public virtual void Execute(TCodeActivity codeActivity, CodeActivityContext executionContext,
			Guid? userId, bool isLogEnabled = true)
		{
			Execute(codeActivity, executionContext, PluginUser.Custom, userId, isLogEnabled);
		}

		public virtual void Execute(TCodeActivity codeActivity, CodeActivityContext executionContext,
			PluginUser user, bool isLogEnabled = true)
		{
			Execute(codeActivity, executionContext, user, null, isLogEnabled);
		}

		public virtual void Execute(TCodeActivity codeActivity, CodeActivityContext executionContext,
			PluginUser user, Guid? userId, bool isLogEnabled = true)
		{
			this.codeActivity = codeActivity;
			this.executionContext = executionContext;

			InitialiseCodeActivity(user, userId);

			try
			{
				tracingService.Trace("Initialising log ...");
				log = new CrmLog(executionContext);
				tracingService.Trace("Finished initialising log.");

				if (isLogEnabled)
				{
					tracingService.Trace("Log enabled.");

					tracingService.Trace("Setting entry class in log ...");
					log.SetEntryClass(typeof(TCodeActivity).FullName);

					tracingService.Trace("Checking regarding to be set in log ...");
					if (context.PrimaryEntityName != null && context.PrimaryEntityName != "none"
						&& context.PrimaryEntityId != Guid.Empty)
					{
						tracingService.Trace("Setting regarding in log ...");
						log.SetRegarding(context.PrimaryEntityName, context.PrimaryEntityId);
					}
				}
			}
			catch (Exception ex)
			{
				throw new InvalidPluginExecutionException("Failed to init log => " + ex.Message, ex);
			}

			try
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Logging execution start ...");
					log.LogExecutionStart();
				}

				tracingService.Trace("Executing step logic ...");
				ExecuteLogic();
				tracingService.Trace("Finished executing step.");
			}
			catch (Exception ex)
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Exception occurred.");
					log.ExecutionFailed();
					tracingService.Trace("Logging exception ...");
					tracingService.Trace(CrmHelpers.BuildExceptionMessage(ex));
					log.Log(ex, context);
				}

				throw new InvalidPluginExecutionException(ex.Message, ex);
			}
			finally
			{
				if (isLogEnabled)
				{
					tracingService.Trace("Logging execution end ...");
					log.LogExecutionEnd();
				}

				tracingService.Trace("Finished executing step.");
			}
		}

		protected abstract void ExecuteLogic();

		protected virtual bool IsContextValid()
		{
			return true;
		}

		protected void InitialiseCodeActivity(PluginUser user = PluginUser.System, Guid? userId = null)
		{
			tracingService = executionContext.GetExtension<ITracingService>();
			context = executionContext.GetExtension<IWorkflowContext>();
			tracingService.Trace("Got context.");
			serviceFactory = executionContext.GetExtension<IOrganizationServiceFactory>();
			tracingService.Trace("Got factory.");

			switch (user)
			{
				case PluginUser.ContextUser:
					userId = context.UserId;
					tracingService.Trace("Running as ContextUser.");
					break;

				case PluginUser.InitiatingUser:
					userId = context.InitiatingUserId;
					tracingService.Trace("Running as InitiatingUserId.");
					break;

				case PluginUser.System:
					userId = null;
					tracingService.Trace("Running as System.");
					break;

				case PluginUser.Custom:
					tracingService.Trace($"Running as {userId}.");
					break;

				default:
					throw new ArgumentOutOfRangeException("user", user, "Step user type is out of range.");
			}

			service = serviceFactory.CreateOrganizationService(userId);
			tracingService.Trace("Got service.");

			if (!IsContextValid())
			{
				throw new InvalidPluginExecutionException("Failed to initialise workflow step due to invalid context.");
			}

			tracingService.Trace($"Initialised step.");
		}
	}

	#endregion

	#region Helpers

	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class Helpers
	{
		/// <summary>
		///     Gets the name of the class by going back frames in the stack trace.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="skipFrames">
		///     [Optional] The number of frames to go back.<br />
		///     If not specified, it will get the first class in the stack that does not equal this one.
		/// </param>
		/// <param name="skipClassName">[Optional] The name of the class to skip if frame are set to '-1'.</param>
		/// <returns>The name of the class</returns>
		public static string GetClassName(int skipFrames = -1, string skipClassName = null)
		{
			Type declaringType = null;
			var currentClass = new StackFrame().GetMethod().ReflectedType;
			skipClassName = string.IsNullOrEmpty(skipClassName) ? currentClass?.FullName : skipClassName;

			if (skipFrames < 0 && !string.IsNullOrEmpty(skipClassName))
			{
				for (var i = 1; i < 100; i++)
				{
					var method = new StackFrame(i).GetMethod();

					if (method == null)
					{
						break;
					}

					declaringType = method.ReflectedType;
					var className = declaringType == null ? "" : declaringType.FullName;

					if (className.Contains(skipClassName))
					{
						continue;
					}

					break;
				}
			}
			else if (skipFrames >= 0)
			{
				declaringType = new StackFrame(skipFrames + 1).GetMethod().ReflectedType;
			}

			return declaringType == null ? "" : declaringType.FullName;
		}

		/// <summary>
		///     Gets the stack trace of the current execution path.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="skipFrames">
		///     [Optional] The number of frames to go back.<br />
		///     If not specified, it will get the first class in the stack that does not equal this one.
		/// </param>
		/// <param name="skipClassName">[Optional] The name of the class to skip if frame are set to '-1'.</param>
		/// <returns>The stack trace</returns>
		public static string GetStackTrace(int skipFrames = -1, string skipClassName = null)
		{
			var currentClass = new StackFrame().GetMethod().DeclaringType;
			skipClassName = string.IsNullOrEmpty(skipClassName) ? currentClass?.FullName : skipClassName;

			if (skipFrames < 0 && !string.IsNullOrEmpty(skipClassName))
			{
				for (var i = 1; i < 100; i++)
				{
					var method = new StackFrame(i).GetMethod();

					if (method == null)
					{
						break;
					}

					var declaringType = method.DeclaringType;
					var className = declaringType == null ? "" : declaringType.FullName;

					if (className.Contains(skipClassName))
					{
						continue;
					}

					skipFrames = i - 1;
					break;
				}
			}
			else if (skipFrames < 0)
			{
				skipFrames = 0;
			}

			return new StackTrace(skipFrames + 1).ToString();
		}

		/// <summary>
		///     Adds the given object to the MemCache. You can't use an offset with a sliding expiration together.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="key">The string key to add this object under.</param>
		/// <param name="item">The object to add.</param>
		/// <param name="offset">
		///     [OPTIONAL] The time after which to remove the object from the cache. Not affected by the sliding
		///     expiration.
		/// </param>
		/// <param name="slidingExpiration">
		///     [OPTIONAL] The duration after which to remove the object from cache, if it was not
		///     accessed for that duration.
		/// </param>
		public static void AddToMemCache(string key, object item, DateTimeOffset? offset = null,
			TimeSpan? slidingExpiration = null)
		{
			if (item == null)
			{
				return;
			}

			ObjectCache cache = MemoryCache.Default;

			RemoveFromMemCache(key);

			if (slidingExpiration != null)
			{
				var policy = new CacheItemPolicy {SlidingExpiration = slidingExpiration.Value};

				if (offset != null)
				{
					policy.AbsoluteExpiration = offset.Value;
				}

				cache.Add(key, item, policy);
			}

			cache.Add(key, item, offset ?? ObjectCache.InfiniteAbsoluteExpiration);
		}

		/// <summary>
		///     Gets the object from the MemCache.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="key">The string key for the object to get.</param>
		public static TItemType GetFromMemCache<TItemType>(string key)
		{
			ObjectCache cache = MemoryCache.Default;

			if (cache.Contains(key))
			{
				return (TItemType) cache.Get(key);
			}

			return default(TItemType);
		}

		/// <summary>
		///     Removes the object from the MemCache.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="key">The string key for the object to remove.</param>
		public static void RemoveFromMemCache(string key)
		{
			ObjectCache cache = MemoryCache.Default;

			if (cache.Contains(key))
			{
				cache.Remove(key);
			}
		}

		private static readonly IDictionary<Type, Func<object, string>> stringRepresentationsMap =
			new ConcurrentDictionary<Type, Func<object, string>>();

		/// <summary>
		///     Add a function that returns a string representing the given type's values to the pool in memory.<br />
		///     This function will be searched for when 'GetStringRepresentation' is called.
		/// </summary>
		/// <param name="evaluator">
		///     A function that takes an object of the given type
		///     and returns a string representation of the values of that type.
		/// </param>
		public static void AddStringRepresentationFunction<T>(Func<T, string> evaluator)
		{
			evaluator.Require();
			stringRepresentationsMap[typeof(T)] = obj => obj is T ? evaluator((T) obj) : null;
		}

		/// <summary>
		///     Gets the function that returns a string representing the given type's values from the pool in memory.<br />
		/// </summary>
		public static Func<object, string> GetStringRepresentationFunction(Type objectType)
		{
			if (objectType == null)
			{
				return null;
			}

			Func<object, string> evaluator;
			stringRepresentationsMap.TryGetValue(objectType, out evaluator);
			return evaluator ?? GetStringRepresentationFunction(objectType.BaseType);
		}

		/// <summary>
		///     Returns a string representing the given object's value.<br />
		///     This can be used in place of 'ToString'.<br />
		///     Custom functions can be defined and added to the pool using 'AddTypeStringRepresentation'.
		/// </summary>
		public static string GetStringRepresentation(object obj)
		{
			if (obj == null)
			{
				return "null";
			}

			StringBuilder builder;

			if (obj is ICollection)
			{
				var collection = (ICollection) obj;
				var enumerator = collection.GetEnumerator();
				builder = new StringBuilder();

				var jumpSize = (int) Math.Ceiling(collection.Count / 5.0);
				var index = 0;
				var skipped = false;

				builder.Append("[");

				while (enumerator.MoveNext())
				{
					if (index % jumpSize == 0 || index == 0)
					{
						if (index > 0)
						{
							builder.Append(",");

							if (skipped)
							{
								builder.Append("..,");
							}
						}

						builder.Append($"{GetStringRepresentation(enumerator.Current)}");
					}
					else
					{
						skipped = true;
					}

					index++;
				}

				builder.Append("]");

				return builder.ToString();
			}

			if (obj is EntityReference)
			{
				var reference = (EntityReference) obj;
				builder = new StringBuilder();

				builder.Append($"{{Logical name = {reference.LogicalName}, Id = {reference.Id}");

				if (!string.IsNullOrEmpty(reference.Name))
				{
					builder.Append($", Name = {reference.Name}");
				}

				builder.Append("}");

				return builder.ToString();
			}

			if (obj.ToString() != obj.GetType().ToString())
			{
				return obj.ToString();
			}

			var function = GetStringRepresentationFunction(obj.GetType());
			return function == null ? obj.ToString() : function(obj);
		}
	}

	/// <summary>
	///     credit: http://blog.codeeffects.com/Article/Generate-Random-Numbers-And-Strings-C-Sharp <br />
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public static class RandomGenerator
	{
		public enum SymbolFlag
		{
			UPPERS = 0,
			LOWERS = 1,
			NUMBERS = 2
		}

		public static string GetRandomString(int length, bool isLetterStart, int numberLetterRatio,
			params string[] symbols)
		{
			var sb = new StringBuilder();

			var digits = symbols.Where(symbol => char.IsDigit(symbol[0])).ToArray();
			var letters = symbols.Where(symbol => char.IsLetter(symbol[0])).ToArray();

			var digitsCount = 0;
			var lettersCount = 0;
			var floatRatio = numberLetterRatio / 100.0;

			for (var i = 0; i < length; i++)
			{
				var filteredSymbols = symbols;

				if (isLetterStart && i == 0)
				{
					lettersCount++;
					filteredSymbols = letters;
				}
				else if (numberLetterRatio > -1)
				{
					if ((lettersCount / (float) length) >= (1 - floatRatio)
						|| (GetRandomNumber(0, 100) <= numberLetterRatio
							&& (digitsCount / (float) length) < floatRatio))
					{
						digitsCount++;
						filteredSymbols = digits;
					}
					else
					{
						lettersCount++;
						filteredSymbols = letters;
					}
				}

				sb.Append(filteredSymbols[GetRandomNumber(0, filteredSymbols.Length)]);
			}

			return sb.ToString();
		}

		public static string GetRandomString(int length, bool isLetterStart, params string[] symbols)
		{
			return GetRandomString(length, isLetterStart, -1, symbols);
		}

		public static string GetRandomString(int length, params string[] symbols)
		{
			return GetRandomString(length, false, -1, symbols);
		}

		public static string GetRandomString(int length, bool isLetterStart, int numberLetterRatio,
			params SymbolFlag[] symbolFlags)
		{
			var array = new List<string>();

			#region Symbol string pool

			var arraySymbolUppers = new[]
									{
										"A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "M", "N", "P", "R", "S", "T", "U",
										"V", "W", "X", "Y",
										"Z"
									};

			var arraySymbolLowers = new[]
									{
										"a", "b", "c", "d", "e", "f", "g", "h", "j", "k", "m", "n", "p", "q", "r", "s", "t", "u",
										"v", "w", "x", "y",
										"z"
									};

			var arrayNumbers = new[]
							   {
								   "0", "2", "3", "4", "5", "6", "8", "9"
							   };

			#endregion

			#region Flag parsers

			if (symbolFlags.Length <= 0)
			{
				symbolFlags = new[] {SymbolFlag.UPPERS, SymbolFlag.LOWERS, SymbolFlag.NUMBERS};
			}

			if (symbolFlags.Contains(SymbolFlag.UPPERS))
			{
				array.AddRange(arraySymbolUppers);
			}

			if (symbolFlags.Contains(SymbolFlag.LOWERS))
			{
				array.AddRange(arraySymbolLowers);
			}

			if (symbolFlags.Contains(SymbolFlag.NUMBERS))
			{
				array.AddRange(arrayNumbers);
			}

			#endregion

			return GetRandomString(length, isLetterStart, numberLetterRatio, array.ToArray());
		}

		public static string GetRandomString(int length, bool isLetterStart, params SymbolFlag[] symbolFlags)
		{
			return GetRandomString(length, isLetterStart, -1, symbolFlags);
		}

		public static string GetRandomString(int length, params SymbolFlag[] symbolFlags)
		{
			return GetRandomString(length, false, -1, symbolFlags);
		}

		public static int GetRandomNumber(int maxNumber = 100)
		{
			return GetRandomNumber(0, maxNumber);
		}

		public static int GetRandomNumber(int minNumber, int maxNumber)
		{
			var b = new byte[4];
			new RNGCryptoServiceProvider().GetBytes(b);
			var seed = (b[0] & 0x7f) << 24 | b[1] << 16 | b[2] << 8 | b[3];
			var r = new Random(seed);

			return r.Next(minNumber, maxNumber);
		}
	}

	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class SlaHelpers
	{
		public static DateTime GetSlaTime(IOrganizationService service, DateTime utcStartTime, double duration,
			DurationUnit durationUnit, int timeZoneShift = 0, List<Entity> cache = null)
		{
			var startTime = utcStartTime.AddMinutes(timeZoneShift);
			var notificationTime = startTime;
			var dayClosingTime = notificationTime.Date;
			var dayStartingTime = notificationTime.Date;

			//set to initial value of the next day at 12:00 AM
			var nextDay = notificationTime.Date.AddDays(1);

			var vacations = GetVacations(service, null, cache);
			var standardWorkingHoursEntity = GetStandardWorkingHours(service, cache); //i.e: 8 hrs

			if (standardWorkingHoursEntity == null)
			{
				throw new InvalidPluginExecutionException(
					"The standard working hours are not defined in the system. Please contact your system administrator");
			}

			var exceptionalWorkingHoursCollection = GetExceptionalWorkingHours(service, cache);
			var durationInHours = duration; //sla of Task Config or Case config

			switch (durationUnit)
			{
				case DurationUnit.Days:
					if (standardWorkingHoursEntity.Contains("ldv_workinghours"))
					{
						durationInHours = durationInHours * double.Parse(standardWorkingHoursEntity["ldv_workinghours"].ToString());
					}
					break;

				case DurationUnit.Hours:
					break;

				case DurationUnit.Minutes:
					durationInHours = duration / 60.0;
					break;

				case DurationUnit.Seconds:
					durationInHours = duration / 60.0 / 60.0;
					break;

				default:
					throw new ArgumentOutOfRangeException("durationUnit", durationUnit, null);
			}

			var currentDayWorkingHours = GetDayWorkingHours(startTime, standardWorkingHoursEntity,
				exceptionalWorkingHoursCollection, timeZoneShift);

			if (currentDayWorkingHours.Contains("ldv_workinghoursend")) //("new_to"))
			{
				var hoursToText = currentDayWorkingHours.FormattedValues["ldv_workinghoursend"];
				var hoursToParts = hoursToText.Split(' ');
				var hoursTo = double.Parse(hoursToParts[0].Split(':')[0]);
				var minutesTo = double.Parse(hoursToParts[0].Split(':')[1]);

				if (hoursToParts[1].ToLower() == "pm" && hoursTo != 12)
				{
					hoursTo += 12;
				}

				if (minutesTo == 30)
				{
					hoursTo += 0.5;
				}
				dayClosingTime = dayClosingTime.AddHours(hoursTo);
			}

			if (currentDayWorkingHours.Contains("ldv_workinghoursstart")) //("new_from"))
			{
				//string hoursFromText = ((Picklist)workingDays.Properties["ld_from"]).name;
				var hoursFromText = currentDayWorkingHours.FormattedValues["ldv_workinghoursstart"];
				var hoursFromParts = hoursFromText.Split(' ');
				var hoursFrom = double.Parse(hoursFromParts[0].Split(':')[0]);
				var minutesFrom = double.Parse(hoursFromParts[0].Split(':')[1]);
				if (hoursFromParts[1].ToLower() == "pm" && hoursFrom != 12)
				{
					hoursFrom += 12;
				}
				if (minutesFrom == 30)
				{
					hoursFrom += 0.5;
				}
				dayStartingTime = dayStartingTime.AddHours(hoursFrom);
			}

			if (!IsVacation(startTime, currentDayWorkingHours, vacations, timeZoneShift) && startTime <= dayClosingTime)
			{
				if (startTime < dayStartingTime)
				{
					startTime = dayStartingTime;
				}

				notificationTime = startTime.AddHours(durationInHours);

				if (notificationTime <= dayClosingTime)
				{
					durationInHours = 0;
				}
				else
				{
					var nextDayRemainingTime = notificationTime - dayClosingTime;

					durationInHours = nextDayRemainingTime.TotalHours;
				}
			}

			while (durationInHours > 0)
			{
				var nextDayStartTime = nextDay;
				var nextDayWorkingHours = GetDayWorkingHours(nextDay, standardWorkingHoursEntity,
					exceptionalWorkingHoursCollection, timeZoneShift);

				while (IsVacation(nextDay, nextDayWorkingHours, vacations, timeZoneShift))
				{
					nextDay = nextDay.AddDays(1);
					nextDayWorkingHours = GetDayWorkingHours(nextDay, standardWorkingHoursEntity,
						exceptionalWorkingHoursCollection, timeZoneShift);
				}

				double nextDayTotalWorkingHours = 0;

				if (nextDayWorkingHours.Contains("ldv_workinghours"))
				{
					nextDayTotalWorkingHours = double.Parse(nextDayWorkingHours["ldv_workinghours"].ToString());
				}

				if (nextDayWorkingHours.Contains("ldv_workinghoursstart"))
				{
					var hoursFromText = nextDayWorkingHours.FormattedValues["ldv_workinghoursstart"];
					var hoursFromParts = hoursFromText.Split(' ');
					var hoursFrom = double.Parse(hoursFromParts[0].Split(':')[0]);
					var minutesFrom = double.Parse(hoursFromParts[0].Split(':')[1]);

					if (hoursFromParts[1].ToLower() == "pm" && hoursFrom != 12)
					{
						hoursFrom += 12;
					}

					if (minutesFrom == 30)
					{
						hoursFrom += 0.5;
					}

					nextDayStartTime = nextDay.AddHours(hoursFrom);
				}

				if (durationInHours <= nextDayTotalWorkingHours)
				{
					notificationTime = nextDayStartTime.AddHours(durationInHours);
					durationInHours = 0;
				}
				else
				{
					nextDay = nextDay.AddDays(1);
					durationInHours = durationInHours - nextDayTotalWorkingHours;
				}
			}

			return notificationTime;
		}

		public static double GetSlaDuration(IOrganizationService service, DateTime utcDateStartingTime,
			DateTime utcDateEndingTime, DurationUnit durationUnit, int timeZoneShift = 0, List<Entity> cache = null)
		{
			var dateStartingTime = utcDateStartingTime.AddMinutes(timeZoneShift);
			var dateEndingTime = utcDateEndingTime.AddMinutes(timeZoneShift);
			var nextDay = dateStartingTime.AddDays(1);
			var durationInMinutes = 0d;
			var dateStartingHours = dateStartingTime.Hour;
			var dateStartingMinutes = dateStartingTime.Minute;

			var dateEndingHours = dateEndingTime.Hour;
			var dateEndingMinutes = dateEndingTime.Minute;

			var vacations = GetVacations(service, dateStartingTime, cache);

			var standardWorkingHours = GetStandardWorkingHours(service, cache);

			if (standardWorkingHours == null)
			{
				throw new InvalidPluginExecutionException(
					"The standard working hours are not defined in the system. Please contact your system administrator");
			}

			var exceptionalWorkingHoursCollection = GetExceptionalWorkingHours(service, cache);

			var nextdayWorkingHours = 0.0;

			if (dateStartingTime < dateEndingTime)
			{
				//For First Day
				var currentDayWorkingHours = GetDayWorkingHours(dateStartingTime, standardWorkingHours,
					exceptionalWorkingHoursCollection, timeZoneShift);

				if (currentDayWorkingHours.Contains("ldv_workinghours"))
				{
					nextdayWorkingHours = double.Parse(currentDayWorkingHours["ldv_workinghours"].ToString());
				}

				if (currentDayWorkingHours.Contains("ldv_workinghoursend"))
				{
					var hoursFromText = currentDayWorkingHours.FormattedValues["ldv_workinghoursstart"];
					var hoursToText = currentDayWorkingHours.FormattedValues["ldv_workinghoursend"];
					var hoursFromParts = hoursFromText.Split(' ');
					var hoursToParts = hoursToText.Split(' ');
					var hoursFrom = double.Parse(hoursFromParts[0].Split(':')[0]);
					var hoursTo = double.Parse(hoursToParts[0].Split(':')[0]);
					var minutesFrom = double.Parse(hoursFromParts[0].Split(':')[1]);
					var minutesTo = double.Parse(hoursToParts[0].Split(':')[1]);

					if (hoursFromParts[1].ToLower() == "pm" && hoursFrom != 12)
					{
						hoursFrom += 12;
					}

					if (minutesFrom == 30)
					{
						hoursFrom += 0.5;
					}

					if (hoursToParts[1].ToLower() == "pm" && hoursTo != 12)
					{
						hoursTo += 12;
					}

					if (minutesTo == 30)
					{
						hoursTo += 0.5;
					}

					if (!IsVacation(dateStartingTime, currentDayWorkingHours, vacations, timeZoneShift))
					{
						double sdt = (dateStartingHours * 60) + dateStartingMinutes;

						if (dateStartingTime.Day != dateEndingTime.Day || dateStartingTime.Month != dateEndingTime.Month ||
							dateStartingTime.Year != dateEndingTime.Year)
						{
							if (sdt < (hoursFrom * 60))
							{
								durationInMinutes += nextdayWorkingHours * 60;
							}
							else if (sdt <= (hoursTo * 60))
							{
								durationInMinutes += ((hoursTo - dateStartingHours) * 60) + ((0 - dateStartingMinutes));
							}
						}
					}
				}

				//For the next Days
				while (nextDay < dateEndingTime &&
					((nextDay.Day != dateEndingTime.Day && nextDay.Month == dateEndingTime.Month &&
						nextDay.Year == dateEndingTime.Year) ||
						(nextDay.Month != dateEndingTime.Month) ||
						(nextDay.Year != dateEndingTime.Year)))
				{
					var nextDayWorkingHours = GetDayWorkingHours(nextDay, standardWorkingHours,
						exceptionalWorkingHoursCollection, timeZoneShift);

					while (IsVacation(nextDay, nextDayWorkingHours, vacations, timeZoneShift))
					{
						nextDay = nextDay.AddDays(1);
						nextDayWorkingHours = GetDayWorkingHours(nextDay, standardWorkingHours,
							exceptionalWorkingHoursCollection, timeZoneShift);
					}

					if (nextDay < dateEndingTime &&
						((nextDay.Day != dateEndingTime.Day && nextDay.Month == dateEndingTime.Month &&
							nextDay.Year == dateEndingTime.Year) ||
							(nextDay.Month != dateEndingTime.Month) ||
							(nextDay.Year != dateEndingTime.Year)))
					{
						if (nextDayWorkingHours.Contains("ldv_workinghours"))
						{
							nextdayWorkingHours = double.Parse(nextDayWorkingHours["ldv_workinghours"].ToString());
						}

						durationInMinutes += nextdayWorkingHours * 60;
						nextDay = nextDay.AddDays(1);
					}
				}

				if (dateStartingTime.Day == dateEndingTime.Day && dateStartingTime.Month == dateEndingTime.Month &&
					dateStartingTime.Year == dateEndingTime.Year)
				{
					currentDayWorkingHours = GetDayWorkingHours(dateStartingTime, standardWorkingHours,
						exceptionalWorkingHoursCollection, timeZoneShift);

					if (!IsVacation(dateStartingTime, currentDayWorkingHours, vacations, timeZoneShift))
					{
						if (currentDayWorkingHours.Contains("ldv_workinghours"))
						{
							nextdayWorkingHours = double.Parse(currentDayWorkingHours["ldv_workinghours"].ToString());
						}

						if (currentDayWorkingHours.Contains("ldv_workinghoursstart") &&
							currentDayWorkingHours.Contains("ldv_workinghoursend"))
						{
							var hoursFromText = currentDayWorkingHours.FormattedValues["ldv_workinghoursstart"];
							var hoursToText = currentDayWorkingHours.FormattedValues["ldv_workinghoursend"];
							var hoursFromParts = hoursFromText.Split(' ');
							var hoursToParts = hoursToText.Split(' ');
							var hoursFrom = double.Parse(hoursFromParts[0].Split(':')[0]);
							var hoursTo = double.Parse(hoursToParts[0].Split(':')[0]);
							var minutesFrom = double.Parse(hoursFromParts[0].Split(':')[1]);
							var minutesTo = double.Parse(hoursToParts[0].Split(':')[1]);

							if (hoursFromParts[1].ToLower() == "pm" && hoursFrom != 12)
							{
								hoursFrom += 12;
							}

							if (minutesFrom == 30)
							{
								hoursFrom += 0.5;
							}

							if (hoursToParts[1].ToLower() == "pm" && hoursTo != 12)
							{
								hoursTo += 12;
							}

							if (minutesTo == 30)
							{
								hoursTo += 0.5;
							}

							if (dateStartingTime.Day == dateEndingTime.Day &&
								dateStartingTime.Month == dateEndingTime.Month &&
								dateStartingTime.Year == dateEndingTime.Year)
							{
								double sdt = (dateStartingHours * 60) + dateStartingMinutes;
								double edt = (dateEndingHours * 60) + dateEndingMinutes;

								if (sdt > (hoursFrom * 60) && edt < (hoursTo * 60))
								{
									durationInMinutes += ((dateEndingHours - dateStartingHours) * 60) + ((dateEndingMinutes - dateStartingMinutes));
								}
								else if (sdt >= (hoursFrom * 60) && edt >= (hoursTo * 60))
								{
									durationInMinutes += ((hoursTo - dateStartingHours) * 60) + ((0 - dateStartingMinutes));
								}
								else if (sdt <= (hoursFrom * 60) && edt < (hoursTo * 60))
								{
									durationInMinutes += ((dateEndingHours - hoursFrom) * 60) + ((dateEndingMinutes - 0));
								}
								else if (sdt < (hoursFrom * 60) && edt >= (hoursTo * 60))
								{
									durationInMinutes += nextdayWorkingHours * 60;
								}
							}
							else
							{
								double edt = (dateEndingHours * 60) + dateEndingMinutes;

								durationInMinutes += ((((edt >= (hoursTo * 60)) ? hoursTo : dateEndingHours) - hoursFrom) * 60) +
									((edt >= (hoursTo * 60)) ? 0 : dateEndingMinutes);
							}
						}
					}
				}
				else if (nextDay.Day == dateEndingTime.Day && nextDay.Month == dateEndingTime.Month &&
					nextDay.Year == dateEndingTime.Year)
				{
					//Last Day
					var lastDayWorkingHours = GetDayWorkingHours(nextDay, standardWorkingHours,
						exceptionalWorkingHoursCollection, timeZoneShift);


					if (!IsVacation(nextDay, lastDayWorkingHours, vacations, timeZoneShift))
					{
						if (lastDayWorkingHours.Contains("ldv_workinghours"))
						{
							nextdayWorkingHours = double.Parse(lastDayWorkingHours["ldv_workinghours"].ToString());
						}

						if (lastDayWorkingHours.Contains("ldv_workinghoursstart") && lastDayWorkingHours.Contains("ldv_workinghoursend"))
						{
							//string hoursFromText = ((Picklist)workingDays.Properties["ld_from"]).name;
							var hoursFromText = lastDayWorkingHours.FormattedValues["ldv_workinghoursstart"];
							var hoursToText = lastDayWorkingHours.FormattedValues["ldv_workinghoursend"];
							var hoursFromParts = hoursFromText.Split(' ');
							var hoursToParts = hoursToText.Split(' ');
							var hoursFrom = double.Parse(hoursFromParts[0].Split(':')[0]);
							var hoursTo = double.Parse(hoursToParts[0].Split(':')[0]);
							var minutesFrom = double.Parse(hoursFromParts[0].Split(':')[1]);
							var minutesTo = double.Parse(hoursToParts[0].Split(':')[1]);

							if (hoursFromParts[1].ToLower() == "pm" && hoursFrom != 12)
							{
								hoursFrom += 12;
							}

							if (minutesFrom == 30)
							{
								hoursFrom += 0.5;
							}

							if (hoursToParts[1].ToLower() == "pm" && hoursTo != 12)
							{
								hoursTo += 12;
							}

							if (minutesTo == 30)
							{
								hoursTo += 0.5;
							}

							if (dateStartingTime.Day == dateEndingTime.Day &&
								dateStartingTime.Month == dateEndingTime.Month &&
								dateStartingTime.Year == dateEndingTime.Year)
							{
								double sdt = (dateStartingHours * 60) + dateStartingMinutes;
								double edt = (dateEndingHours * 60) + dateEndingMinutes;

								if (sdt > (hoursFrom * 60) && edt < (hoursTo * 60))
								{
									durationInMinutes += ((dateEndingHours - dateStartingHours) * 60) + ((dateEndingMinutes - dateStartingMinutes));
								}
								else if (sdt >= (hoursFrom * 60) && edt >= (hoursTo * 60))
								{
									durationInMinutes += ((hoursTo - dateStartingHours) * 60) + ((0 - dateStartingMinutes));
								}
								else if (sdt <= (hoursFrom * 60) && edt < (hoursTo * 60))
								{
									durationInMinutes += ((dateEndingHours - hoursFrom) * 60) + ((dateEndingMinutes - 0));
								}
								else if (sdt < (hoursFrom * 60) && edt >= (hoursTo * 60))
								{
									durationInMinutes += nextdayWorkingHours * 60;
								}
							}
							else
							{
								double edt = (dateEndingHours * 60) + dateEndingMinutes;

								durationInMinutes += ((((edt >= (hoursTo * 60)) ? hoursTo : dateEndingHours) - hoursFrom) * 60) +
									((edt >= (hoursTo * 60)) ? 0 : dateEndingMinutes);
							}
						}
					}
				}
			}

			//To return the duration
			if (durationInMinutes == 0)
			{
				return 0;
			}

			double returnDuration;

			switch (durationUnit)
			{
				case DurationUnit.Days:
					returnDuration = durationInMinutes / 60.0 / 24.0;
					break;

				case DurationUnit.Hours:
					returnDuration = durationInMinutes / 60.0;
					break;

				case DurationUnit.Minutes:
					returnDuration = durationInMinutes;
					break;

				case DurationUnit.Seconds:
					returnDuration = durationInMinutes * 60.0;
					break;

				default:
					throw new ArgumentOutOfRangeException(nameof(durationUnit), durationUnit, null);
			}

			return returnDuration < 0 ? 0 : returnDuration;
		}

		private static List<Entity> GetVacations(IOrganizationService service, DateTime? date, List<Entity> cache)
		{
			if (cache == null)
			{
				cache = LoadWorkingHoursRecords(service);
			}

			return cache?.Where(
				entity => entity.LogicalName == "ldv_vacationdays"
					&&
					entity.GetAttributeValue<DateTime>("ldv_vacationenddate").ToLocalTime().Date
						>= (date ?? DateTime.Now).Date).ToList();
		}

		private static Entity GetStandardWorkingHours(IOrganizationService service, List<Entity> cache)
		{
			if (cache == null)
			{
				cache = LoadWorkingHoursRecords(service);
			}

			return cache?.FirstOrDefault(
				entity => entity.LogicalName == "ldv_workinghours"
					&& entity.GetAttributeValue<OptionSetValue>("ldv_type") != null
					&& entity.GetAttributeValue<OptionSetValue>("ldv_type").Value == 1);
		}

		private static List<Entity> GetExceptionalWorkingHours(IOrganizationService service, List<Entity> cache)
		{
			if (cache == null)
			{
				cache = LoadWorkingHoursRecords(service);
			}

			return cache?.Where(
				entity => entity.LogicalName == "ldv_workinghours"
					&& entity.GetAttributeValue<OptionSetValue>("ldv_type") != null
					&& entity.GetAttributeValue<OptionSetValue>("ldv_type").Value == 2).ToList();
		}

		private static Entity GetDayWorkingHours(DateTime day, Entity standardWorkingHours,
			List<Entity> exceptionalWorkingHours,
			int timeZoneShift)
		{
			var currentDayWorkingHours = standardWorkingHours;

			if (exceptionalWorkingHours == null || exceptionalWorkingHours.Count <= 0)
			{
				return currentDayWorkingHours;
			}

			foreach (var exceptionalRecord in exceptionalWorkingHours)
			{
				var endDateNode = exceptionalRecord.Contains("ldv_dateto")
					? ((DateTime) exceptionalRecord["ldv_dateto"]).AddMinutes(timeZoneShift).ToString()
					: string.Empty;
				var startDateNode = exceptionalRecord.Contains("ldv_datefrom")
					? ((DateTime) exceptionalRecord["ldv_datefrom"]).AddMinutes(timeZoneShift).ToString()
					: string.Empty;

				if (string.IsNullOrEmpty(endDateNode) || string.IsNullOrEmpty(startDateNode))
				{
					continue;
				}

				var endDate = DateTime.Parse(endDateNode).AddHours(-1 * DateTime.Parse(endDateNode).Hour);
				var startDate = DateTime.Parse(startDateNode).AddHours(-1 * DateTime.Parse(startDateNode).Hour);

				if ((day >= startDate && day <= endDate)
					|| (day.Day == startDate.Day && day.Year == startDate.Year && day.Month == startDate.Month)
					|| (day.Day == endDate.Day && day.Year == endDate.Year && day.Month == endDate.Month))
				{
					currentDayWorkingHours = exceptionalRecord;
					return currentDayWorkingHours;
				}
			}

			return currentDayWorkingHours;
		}

		private static bool IsVacation(DateTime notificationTime, Entity workingDays, List<Entity> vactionCollection,
			int timeZoneShift)
		{
			var isVacation = false;

			var currentDayAttribute = "ldv_" + notificationTime.DayOfWeek.ToString().ToLower();

			if (workingDays?[currentDayAttribute] != null && (bool) workingDays[currentDayAttribute] == false)
			{
				isVacation = true;
			}
			else
			{
				if (vactionCollection.Count <= 0)
				{
					return isVacation;
				}

				foreach (var vacation in vactionCollection)
				{
					var endDateNode = vacation.Contains("ldv_vacationenddate")
						? ((DateTime) vacation["ldv_vacationenddate"]).AddMinutes(timeZoneShift).ToString()
						: string.Empty;
					var startDateNode = vacation.Contains("ldv_vacationstartdate")
						? ((DateTime) vacation["ldv_vacationstartdate"]).AddMinutes(timeZoneShift).ToString()
						: string.Empty;

					if (string.IsNullOrEmpty(endDateNode) || string.IsNullOrEmpty(startDateNode))
					{
						continue;
					}

					var endDate = DateTime.Parse(endDateNode).AddHours(-1 * DateTime.Parse(endDateNode).Hour);
					var startDate = DateTime.Parse(startDateNode).AddHours(-1 * DateTime.Parse(startDateNode).Hour);

					if ((notificationTime >= startDate && notificationTime <= endDate)
						|| (notificationTime.Day == startDate.Day && notificationTime.Year == startDate.Year &&
							notificationTime.Month == startDate.Month)
						|| (notificationTime.Day == endDate.Day && notificationTime.Year == endDate.Year &&
							notificationTime.Month == endDate.Month))
					{
						isVacation = true;
						break;
					}
				}
			}

			return isVacation;
		}

		public static List<Entity> LoadWorkingHoursRecords(IOrganizationService service)
		{
			var cache = new List<Entity>();

			#region Working hours

			var query = new QueryExpression
						{
							EntityName = "ldv_workinghours",
							ColumnSet = new ColumnSet(true)
						};

			var filter = new FilterExpression {FilterOperator = LogicalOperator.And};
			filter.Conditions.Add(new ConditionExpression("statecode", ConditionOperator.Equal, "Active"));
			query.Criteria = filter;

			var result = service.RetrieveMultiple(query).Entities;

			if (result.Any())
			{
				cache.AddRange(result);
			}

			#endregion

			#region Vacations

			query = new QueryExpression
					{
						EntityName = "ldv_vacationdays",
						ColumnSet = new ColumnSet(true)
					};

			result = service.RetrieveMultiple(query).Entities;

			if (result.Any())
			{
				cache.AddRange(result);
			}

			#endregion

			return cache;
		}
	}

	public enum DurationUnit
	{
		Days = 4,
		Hours = 1,
		Minutes = 2,
		Seconds = 3
	}

	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class DateTimeHelpers
	{
		/// <summary>
		///     Gets the number of seconds that has passed since 1/1/1970 12AM.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="endDate">[OPTIONAL=UtcNow] The date to count to.</param>
		/// <returns>Number of seconds.</returns>
		public static long GetSecondsSinceEpoch(DateTime? endDate = null)
		{
			return (long) (endDate ?? DateTime.UtcNow).Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;
		}

		/// <summary>
		///     Gets the day occurence of month.<br />
		///     Credit: http://stackoverflow.com/a/18621645/1919456
		/// </summary>
		public static DateTime? GetDayOccurrenceOfMonth(DateTime dateOfMonth, DayOfWeek dayOfWeek, bool isLastOccurrence)
		{
			return GetDayOccurrenceOfMonth(dateOfMonth, dayOfWeek, 5, isLastOccurrence);
		}

		/// <summary>
		///     Gets the day occurence of month.<br />
		///     Credit: http://stackoverflow.com/a/18621645/1919456
		/// </summary>
		public static DateTime? GetDayOccurrenceOfMonth(DateTime dateOfMonth, DayOfWeek dayOfWeek, int occurrence,
			bool isLastOccurrence)
		{
			occurrence.RequireInRange(1, 5, "occurrence", "Occurrence must be greater than zero and less than 6.");

			if (isLastOccurrence)
			{
				occurrence = 5;
			}

			// Change to first day of the month
			var dayOfMonth = dateOfMonth.AddDays(1 - dateOfMonth.Day);

			// Find first dayOfWeek of this month;
			if (dayOfMonth.DayOfWeek > dayOfWeek)
			{
				dayOfMonth = dayOfMonth.AddDays(7 - (int) dayOfMonth.DayOfWeek + (int) dayOfWeek);
			}
			else
			{
				dayOfMonth = dayOfMonth.AddDays((int) dayOfWeek - (int) dayOfMonth.DayOfWeek);
			}

			// add 7 days per occurrence
			dayOfMonth = dayOfMonth.AddDays(7 * (occurrence - 1));

			// make sure this occurrence is within the original month
			if (dayOfMonth.Month == dateOfMonth.Month)
			{
				return dayOfMonth;
			}
			else
			{
				if (isLastOccurrence)
				{
					return dayOfMonth.AddDays(-7);
				}

				return null;
			}
		}
	}

	/// <summary>
	///     credit: http://pietschsoft.com/post/2008/02/net-35-json-serialization-using-the-datacontractjsonserializer <br />
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class SerialiserHelpers
	{
		public static string SerialiseBase64(object obj)
		{
			using (var stream = new MemoryStream())
			{
				using (var writer = XmlDictionaryWriter.CreateBinaryWriter(stream))
				{
					var serializer = new DataContractSerializer(obj.GetType());
					serializer.WriteObject(writer, obj);
					return Convert.ToBase64String(stream.ToArray());
				}
			}
		}

		public static T DeserialiseBase64<T>(string base64)
		{
			using (var stream = new MemoryStream(Convert.FromBase64String(base64)))
			{
				using (var reader = XmlDictionaryReader
					.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max))
				{
					var serializer = new DataContractSerializer(typeof(T));
					return (T) serializer.ReadObject(reader);
				}
			}
		}

		public static string SerialiseXml<T>(T obj)
		{
			using (var stream = new MemoryStream())
			{
				var serializer = new DataContractSerializer(obj.GetType());
				serializer.WriteObject(stream, obj);
				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		public static T DeserializeXml<T>(string xml)
		{
			using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
			{
				var serializer = new DataContractSerializer(typeof(T));
				return (T) serializer.ReadObject(stream);
			}
		}

		/// <summary>
		///     Author: Ramy Victor
		/// </summary>
		public static string SerializeObject<T>(T serializableObject)
		{
			using (var stream = new MemoryStream())
			{
				var serializer = new XmlSerializer(typeof(T));
				serializer.Serialize(stream, serializableObject);
				stream.Position = 0;
				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}


		/// <summary>
		///     Author: Ramy Victor
		/// </summary>
		public static T DeserializeObject<T>(string xml)
		{
			using (var read = new StringReader(xml))
			{
				using (XmlReader reader = new XmlTextReader(read))
				{
					var serializer = new XmlSerializer(typeof(T));
					return (T) serializer.Deserialize(reader);
				}
			}
		}

		public static string SerialiseSimpleJson(IDictionary<string, string> dictionary, bool isUnformatted = false)
		{
			return "{" + (isUnformatted ? "" : "\r\n") + dictionary
				.Select(pair => string.Format((isUnformatted ? "" : "\t") + "\"{0}\":\"{1}\"", pair.Key, pair.Value.StringLiteral()))
				.Aggregate((e1, e2) => e1 + "," + (isUnformatted ? "" : "\r\n") + e2) + (isUnformatted ? "" : "\r\n") + "}";
		}

		public static IDictionary<string, string> DeserialiseSimpleJson(string json)
		{
			var matches = Regex.Matches(json, @"""(.*?)"":""(.*?)""");

			if (matches.Count <= 0)
			{
				throw new Exception("JSON is poorly formatted.");
			}

			var dictionary = new Dictionary<string, string>();

			foreach (Match match in matches)
			{
				var groups = match.Groups;

				if (groups.Count < 3)
				{
					throw new Exception("JSON is poorly formatted.");
				}

				dictionary.Add(groups[1].Value, groups[2].Value.StringUnLiteral());
			}

			return dictionary;
		}

		public static string SerialiseJson<T>(T serializableObject)
		{
			using (var memoryStream = new MemoryStream())
			{
				var serializer = new DataContractJsonSerializer(typeof(T));
				serializer.WriteObject(memoryStream, serializableObject);
				return Encoding.Default.GetString(memoryStream.ToArray());
			}
		}
	}

	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public class RetrievePagingInfo
	{
		public string Cookie;
		public int NextPage = 1;
		public int RecordCountLimit = -1;
		public bool IsMoreRecords = true;
	}

	/// <summary>
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class CrmHelpers
	{
		public class EntityComparer : IEqualityComparer<Entity>
		{
			public bool Equals(Entity x, Entity y)
			{
				return x.Id == y.Id;
			}

			public int GetHashCode(Entity obj)
			{
				return obj.Id.GetHashCode();
			}
		}

		public class EntityRefComparer : IEqualityComparer<EntityReference>
		{
			public bool Equals(EntityReference x, EntityReference y)
			{
				return x.Id == y.Id;
			}

			public int GetHashCode(EntityReference obj)
			{
				return obj.Id.GetHashCode();
			}
		}

		public class StageInfo
		{
			public Guid Id { get; set; }
			public string Name { get; set; }
			public int Index { get; set; }
		}

		public enum EntityAttribute
		{
			LogicalName,
			SchemaName,
			PrimaryIdAttribute,
			PrimaryNameAttribute,
			ObjectTypeCode,
			IsActivity
		}

		public enum FieldAttribute
		{
			OptionSet
		}

		public enum RelationAttribute
		{
			SchemaName,
			RelationshipType,
			ReferencedEntity,
			ReferencedAttribute,
			ReferencingEntity,
			ReferencingAttribute,
			IntersectEntityName,
			Entity1LogicalName,
			Entity1IntersectAttribute,
			Entity2LogicalName,
			Entity2IntersectAttribute
		}

		public enum RelationType
		{
			OneToManyRelationships,
			ManyToOneRelationships,
			ManyToManyRelationships
		}

		public class BulkResponse
		{
			public OrganizationResponse Response;
			public Type RequestType;
			public Type ResponseType;
			public OrganizationServiceFault Fault;
			public string FaultMessage;
		}

		/// <summary>
		///     Returns all steps in the process, in order, with their names.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="service">The service.</param>
		/// <param name="processId">The process identifier.</param>
		public static List<StageInfo> GetStages(IOrganizationService service, Guid processId)
		{
			var process = service.Retrieve("workflow", processId, new ColumnSet("xaml"));

			if (!process.Contains("xaml"))
			{
				return null;
			}

			var doc = new XmlDocument();
			doc.LoadXml((string) process["xaml"]);

			var stepNodes = doc.SelectNodes("//*[local-name()='Workflow']/*[local-name()='ActivityReference']" +
				"/*[local-name()='ActivityReference.Properties']/*[local-name()='Collection']" +
				"/*[local-name()='ActivityReference']/*[local-name()='ActivityReference.Properties']" +
				"/*[local-name()='Collection']/*[local-name()='StepLabel']");

			var steps = new List<StageInfo>();

			if (stepNodes == null || stepNodes.Count <= 0)
			{
				return null;
			}

			for (var i = 0; i < stepNodes.Count; i++)
			{
				var idText = stepNodes[i].SelectSingleNode("../../*[local-name()='String' and @*[local-name()='Key']='StageId']");
				var id = idText?.InnerText;
				var labelNode = stepNodes[i].SelectSingleNode("@Description");
				var label = labelNode?.InnerText;

				if (id != null)
				{
					steps.Add(new StageInfo
							  {
								  Id = Guid.Parse(id),
								  Name = label,
								  Index = i
							  });
				}
			}

			return steps.Any() ? steps : null;
		}

		/// <summary>
		///     Get the value of the primary name field of the record.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string GetRecordName(IOrganizationService service, Entity record)
		{
			return record.GetAttributeValue<string>(
				GetEntityAttribute<string>(service, record.LogicalName, EntityAttribute.PrimaryNameAttribute));
		}

		/// <summary>
		///     Get the value of the primary name field of the record.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string GetRecordName(IOrganizationService service, string logicalName, Guid id,
			string primaryNameField = null)
		{
			var nameField = string.IsNullOrEmpty(primaryNameField)
				? GetEntityAttribute<string>(service, logicalName, EntityAttribute.PrimaryNameAttribute)
				: primaryNameField;
			return service.Retrieve(logicalName, id, new ColumnSet(nameField)).GetAttributeValue<string>(nameField);
		}

		public static bool IsRecordExists(IOrganizationService service, string logicalName, Guid guid, bool isActivity = false)
		{
			var query = new QueryByAttribute(logicalName);
			query.AddAttributeValue(isActivity ? "activityid" : logicalName + "id", guid);
			query.ColumnSet = new ColumnSet(false);
			return service.RetrieveMultiple(query).Entities.Any();
		}

		/// <summary>
		///     Get the total number of records returned by the given query using a modded binary search algorithm.<br />
		///     It is recommended that the query's column-set is set to 'false' for speed.<br />
		///     Please note that the 'PageInfo' object in the query will be overwritten.<br />
		///     This does NOT work with CRM Online.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static int GetRecordsCount(IOrganizationService service, QueryExpression query, int countPerPage = 5000,
			string cookie = null, int lowerPageLimit = 1, int upperPageLimit = int.MaxValue)
		{
			var minPage = lowerPageLimit;
			var minPageCount = GetCountInPage(service, query, cookie, minPage, countPerPage);

			// first page is already partially filled or empty, so there are no records after for sure
			if (minPageCount >= 0 && minPageCount < countPerPage)
			{
				return minPageCount;
			}

			var maxPage = minPage;
			int maxPageCount;

			// get max page with partial fill or no records, after which there are surely no records
			// jump pages using an exponent of 2 because it's more likely that there are low number of pages than high
			while (true)
			{
				maxPage = maxPage * 2;
				maxPage = Math.Min(maxPage, upperPageLimit);
				maxPageCount = GetCountInPage(service, query, cookie, maxPage, countPerPage);

				// a filled page is considered a minimum, so set it to reduce search range
				if (maxPageCount == countPerPage)
				{
					minPage = maxPage;
					minPageCount = maxPageCount;
				}
				else
				{
					break;
				}
			}

			var isMaxNextToMin = (minPage + 1) == maxPage;
			var isMaxPageEmpty = maxPageCount == 0;
			var isMaxPagePartial = maxPageCount > 0 && maxPageCount < countPerPage;

			// max page is next to min full page, and is empty or partially filled, so we have reached the end
			if (isMaxPagePartial || (isMaxPageEmpty && isMaxNextToMin))
			{
				return (maxPage - 1) * countPerPage + maxPageCount;
			}

			int currentPage;
			int currentPageCount;

			while (true)
			{
				// get the current page in the middle point between min and max
				currentPage = (int) Math.Ceiling((maxPage + minPage) / 2d);

				// if current is not min or max, get its count
				if (currentPage != minPage && currentPage != maxPage)
				{
					currentPageCount = GetCountInPage(service, query, cookie, currentPage, countPerPage);
				}
				else
				{
					currentPageCount = currentPage == minPage ? minPageCount : maxPageCount;
				}

				var isCurrentNextToMin = (minPage + 1) == currentPage;
				var isCurrentNextToMax = (maxPage - 1) == currentPage;
				var isCurrentPageEmpty = currentPageCount == 0;
				var isCurrentPagePartial = currentPageCount > 0 && currentPageCount < countPerPage;
				var isCurrentPageFull = currentPageCount == countPerPage;

				// current page is next to min full page or max empty page
				// and is empty or partially filled, so we have reached the end
				if (isCurrentPagePartial
					|| (isCurrentPageEmpty && isCurrentNextToMin) || (isCurrentPageFull && isCurrentNextToMax))
				{
					break;
				}

				if (isCurrentPageEmpty)
				{
					maxPage = currentPage;
				}

				if (isCurrentPageFull)
				{
					minPage = currentPage;
				}
			}

			return (currentPage - 1) * countPerPage + currentPageCount;
		}

		internal static int GetCountInPage(IOrganizationService service, QueryExpression query, string cookie = null,
			int page = 1, int countPerPage = 5000)
		{
			query.PageInfo = query.PageInfo ?? new PagingInfo();
			query.PageInfo.Count = countPerPage;
			query.PageInfo.PageNumber = page;
			query.PageInfo.PagingCookie = query.PageInfo.PagingCookie ?? cookie;
			query.ColumnSet = query.ColumnSet ?? new ColumnSet(false);
			var result = service.RetrieveMultiple(query);
			query.PageInfo.PagingCookie = result.PagingCookie ?? cookie;
			return result.Entities.Count;
		}

		/// <summary>
		///     Gets records from CRM using the query given.<br />
		///     Pass '-1' for limit to get all records, and pass '-1' to page to ignore pages.
		///     The cookie is saved in the query itself during retrieval, so either save the cookie somewhere
		///     and reset it before passing it here, or simply reuse the query for next pages.<br />
		///     You should not skip pages with CRM Online.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static List<Entity> GetRecords(IOrganizationService service, QueryExpression query,
			int limit = -1, int page = -1)
		{
			query.PageInfo =
				new PagingInfo
				{
					PageNumber = page <= 0
						? (query.PageInfo == null || query.PageInfo.PageNumber <= 0
							? 1
							: query.PageInfo.PageNumber)
						: page,
					Count = limit <= 0
						? (query.PageInfo == null || query.PageInfo.Count <= 0
							? 5000
							: query.PageInfo.Count)
						: limit
				};

			limit = limit <= 0 ? int.MaxValue : limit;

			EntityCollection records;
			var entities = new List<Entity>();

			// get all records
			do
			{
				// fetch the records
				records = service.RetrieveMultiple(query);

				// next time get the next bundle of records
				query.PageInfo.PagingCookie = records.PagingCookie;
				query.PageInfo.PageNumber++;

				// add to existing list
				entities.AddRange(records.Entities);
			} while (records.MoreRecords && entities.Count < limit && page <= 0);

			return entities.ToList();
		}

		/// <summary>
		///     Executes given requests in bulk. The returned value should only be taken into consideration
		///     if 'isReturnResponses' is 'true'.<br />
		///     The handler takes 'current batch index (1, 2 ... etc.), total batch count, responses' as parameters.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static Dictionary<OrganizationRequest, BulkResponse> ExecuteBulk(IOrganizationService service,
			List<OrganizationRequest> requests,
			bool isReturnResponses = false, int batchSize = 1000, bool isContinueOnError = true,
			Action<int, int, IDictionary<OrganizationRequest, BulkResponse>> bulkFinishHandler = null)
		{
			var bulkRequest = new ExecuteMultipleRequest
							  {
								  Requests = new OrganizationRequestCollection(),
								  Settings = new ExecuteMultipleSettings
											 {
												 ContinueOnError = isContinueOnError,
												 ReturnResponses = isReturnResponses
											 }
							  };

			var responses = new Dictionary<OrganizationRequest, BulkResponse>();
			var perBulkResponses = new Dictionary<OrganizationRequest, BulkResponse>();

			var batchCount = Math.Ceiling(requests.Count / (double) batchSize);

			// take bulk size only for each iteration
			for (var i = 0; i < batchCount; i++)
			{
				// clear the previous batch
				bulkRequest.Requests.Clear();
				perBulkResponses.Clear();

				// take batches
				bulkRequest.Requests.AddRange(requests.Skip(i * batchSize).Take(batchSize));
				var bulkResponses = (ExecuteMultipleResponse) service.Execute(bulkRequest);

				// no need to build a response
				if (!isReturnResponses)
				{
					// break on error and no 'continue on error' option
					if (!isContinueOnError && (bulkResponses.IsFaulted || bulkResponses.Responses.Any(e => e.Fault != null)))
					{
						break;
					}
					else
					{
						bulkFinishHandler?.Invoke(i + 1, (int) batchCount, perBulkResponses);
						continue;
					}
				}

				for (var j = 0; j < bulkResponses.Responses.Count; j++)
				{
					var request = bulkRequest.Requests[j];
					var bulkResponse = bulkResponses.Responses[j];
					var fault = bulkResponse.Fault;
					string faultMessage = null;

					// build fault message
					if (fault != null)
					{
						var builder = new StringBuilder();
						builder.AppendFormat("Message: \"{0}\", code: {1}", fault.Message, fault.ErrorCode);

						if (fault.TraceText != null)
						{
							builder.AppendFormat(", trace: \"{0}\"", fault.TraceText);
						}

						if (fault.InnerFault != null)
						{
							builder.AppendFormat(", inner message: \"{0}\", inner code: {1}", fault.InnerFault.Message,
								fault.InnerFault.ErrorCode);

							if (fault.InnerFault.TraceText != null)
							{
								builder.AppendFormat(", trace: \"{0}\"", fault.InnerFault.TraceText);
							}
						}

						faultMessage = builder.ToString();
					}

					var response = new BulkResponse
								   {
									   RequestType = request.GetType(),
									   Response = bulkResponse.Response,
									   ResponseType = bulkResponse.Response == null ? null : bulkResponse.Response.GetType(),
									   Fault = fault,
									   FaultMessage = faultMessage
								   };
					responses[request] = response;
					perBulkResponses[request] = response;
				}

				bulkFinishHandler?.Invoke(i + 1, (int) batchCount, perBulkResponses);

				// break on error and no 'continue on error' option
				if (!isContinueOnError && (bulkResponses.IsFaulted || bulkResponses.Responses.Any(e => e.Fault != null)))
				{
					break;
				}
			}

			return responses;
		}

		#region Metadata helpers

		public static bool IsFieldExistInEntity(IOrganizationService service, string entityName, string fieldName)
		{
			return MetadataHelpers.IsFieldExistInEntity(service, entityName, fieldName);
		}

		public static List<EntityMetadata> GetEntities(IOrganizationService service, params EntityAttribute[] attributes)
		{
			return MetadataHelpers.GetEntities(service, attributes);
		}

		public static List<EntityMetadata> GetEntities(IOrganizationService service, params string[] attributes)
		{
			return MetadataHelpers.GetEntities(service, attributes);
		}

		public static T GetEntityAttribute<T>(IOrganizationService service, string entityName,
			EntityAttribute attribute)
		{
			return MetadataHelpers.GetEntityAttribute<T>(service, entityName, attribute);
		}

		public static string GetEntityNameUsingTypeCode(IOrganizationService service, int typeCode)
		{
			return MetadataHelpers.GetEntityNameUsingTypeCode(service, typeCode);
		}

		public static T GetFieldAttribute<T>(IOrganizationService service, string entityName, string fieldName,
			FieldAttribute attribute)
		{
			return MetadataHelpers.GetFieldAttribute<T>(service, entityName, fieldName, attribute);
		}

		public static List<RelationshipMetadataBase> GetCustomRelationships(IOrganizationService service,
			string entityName, RelationType[] types, RelationAttribute[] attributes)
		{
			return MetadataHelpers.GetCustomRelationships(service, entityName, types, attributes);
		}

		public static string GetOptionSetLabel(IOrganizationService service, string entityName, string fieldName,
			int value)
		{
			return MetadataHelpers.GetOptionSetLabel(service, entityName, fieldName, value);
		}

		#endregion

		#region Relations helpers

		private static QueryExpression BuildRetrieveQuery(Entity entity, IOrganizationService service,
			string fromEntityName, string toEntityName, string fromFieldName, string toFieldName,
			string idFieldName, string intersectIdFieldName, FilterExpression filter = null, params string[] attributes)
		{
			// create the query taking into account paging
			var query = new QueryExpression(fromEntityName);
			query.LinkEntities.Add(new LinkEntity(fromEntityName, toEntityName, fromFieldName, toFieldName, JoinOperator.Inner));
			query.LinkEntities[0].EntityAlias = "linkedEntityAlias";
			query.Criteria.AddCondition("linkedEntityAlias", intersectIdFieldName, ConditionOperator.Equal, entity[idFieldName]);

			if (filter != null)
			{
				query.Criteria.AddFilter(filter);
			}

			if (attributes.Length == 1 && attributes[0] == "*")
			{
				query.ColumnSet = new ColumnSet(true);
			}
			else if (attributes.Length > 0)
			{
				query.ColumnSet = new ColumnSet(attributes);
			}
			else
			{
				query.ColumnSet = new ColumnSet(false);
			}

			return query;
		}

		private static List<Entity> GetRecords(Entity entity, IOrganizationService service,
			string fromEntityName, string toEntityName, string fromFieldName, string toFieldName,
			string idFieldName, string intersectIdFieldName, int limit = -1, int page = -1,
			FilterExpression filter = null, params string[] attributes)
		{
			return GetRecords(service, BuildRetrieveQuery(entity, service, fromEntityName, toEntityName,
				fromFieldName, toFieldName, idFieldName, intersectIdFieldName, filter, attributes), limit, page);
		}

		public static List<Entity> GetRelatedRecords(IOrganizationService service, Entity entity, string idFieldName,
			RelationType[] relationTypes, params string[] attributes)
		{
			var related = new List<Entity>();

			if (relationTypes == null || relationTypes.Length <= 0)
			{
				return related;
			}

			var relations = GetCustomRelationships(service, entity.LogicalName, relationTypes,
				new[]
				{
					RelationAttribute.SchemaName, RelationAttribute.RelationshipType,
					RelationAttribute.ReferencedEntity, RelationAttribute.ReferencedAttribute,
					RelationAttribute.ReferencingEntity, RelationAttribute.ReferencingAttribute,
					RelationAttribute.IntersectEntityName,
					RelationAttribute.Entity1LogicalName, RelationAttribute.Entity1IntersectAttribute,
					RelationAttribute.Entity2LogicalName, RelationAttribute.Entity2IntersectAttribute
				});

			if (relationTypes.Contains(RelationType.ManyToManyRelationships))
			{
				var manyToMany = relations.Where(
					relation => relation.RelationshipType == RelationshipType.ManyToManyRelationship)
					.Cast<ManyToManyRelationshipMetadata>();

				foreach (var rel in manyToMany)
				{
					var entity2Name = rel.Entity1LogicalName == entity.LogicalName
						? rel.Entity2LogicalName
						: rel.Entity1LogicalName;
					var entity2Id = rel.Entity1LogicalName == entity.LogicalName
						? rel.Entity2IntersectAttribute
						: rel.Entity1IntersectAttribute;
					var intersectEntity = rel.IntersectEntityName;

					related.AddRange(GetRecords(entity, service, entity2Name, intersectEntity, entity2Id,
						entity2Id, idFieldName, idFieldName, -1, -1, null, attributes));
				}
			}

			if (relationTypes.Contains(RelationType.OneToManyRelationships))
			{
				var oneToMany = relations.Where(
					relation => relation.RelationshipType == RelationshipType.OneToManyRelationship)
					.Cast<OneToManyRelationshipMetadata>();

				foreach (var rel in oneToMany)
				{
					var entity2Name = rel.ReferencedEntity == entity.LogicalName
						? rel.ReferencingEntity
						: rel.ReferencedEntity;
					var entity2LookupName = rel.ReferencedEntity == entity.LogicalName
						? rel.ReferencingAttribute
						: rel.ReferencedAttribute;

					related.AddRange(GetRecords(entity, service, entity2Name, entity.LogicalName,
						entity2LookupName, idFieldName, idFieldName, idFieldName, -1, -1, null, attributes));
				}
			}

			return related;
		}

		#endregion

		#region User helpers

		public static List<Guid> GetTeamMembers(IOrganizationService service, Guid teamId)
		{
			var svcContext = new OrganizationServiceContext(service);
			var members = (from user in svcContext.CreateQuery("systemuser")
						   join member in svcContext.CreateQuery("teammembership")
							   on user["systemuserid"] equals member["systemuserid"]
						   join teamQ in svcContext.CreateQuery("team")
							   on member["teamid"] equals teamQ["teamid"]
						   where teamQ["teamid"].Equals(teamId)
						   select (Guid) user["systemuserid"]).ToList();

			return members;
		}

		public static List<Guid> GetQueueMembers(IOrganizationService service, Guid queueId)
		{
			var svcContext = new OrganizationServiceContext(service);
			var members = (from user in svcContext.CreateQuery("systemuser")
						   join member in svcContext.CreateQuery("queuemembership")
							   on user["systemuserid"] equals member["systemuserid"]
						   join queue in svcContext.CreateQuery("queue")
							   on member["queueid"] equals queue["queueid"]
						   where queue["queueid"].Equals(queueId)
						   select (Guid) user["systemuserid"]).ToList();

			return members;
		}

		/// <summary>
		///     From the SDK: http://msdn.microsoft.com/en-us/library/hh670609.aspx <br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static int GetPreferredLangCode(IOrganizationService service, EntityReference record)
		{
			if (record.LogicalName == "systemuser")
			{
				var userSettingsQuery = new QueryExpression("usersettings");
				userSettingsQuery.ColumnSet.AddColumns("uilanguageid", "systemuserid");
				userSettingsQuery.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, record.Id);
				var userSettings = service.RetrieveMultiple(userSettingsQuery);

				if (userSettings.Entities.Count > 0)
				{
					var code = (int) userSettings.Entities[0]["uilanguageid"];
					return code <= 0 ? 1033 : code;
				}
			}
			else if (record.LogicalName == "account"
				|| record.LogicalName == "contact")
			{
				Entity result;

				try
				{
					result = service.Retrieve(record.LogicalName, record.Id,
						new ColumnSet("ldv_preferredcommunicationlanguage"));
				}
				catch
				{
					// field does not exist
					return 1033;
				}

				if (!result.Contains("ldv_preferredcommunicationlanguage"))
				{
					return 1033;
				}

				return ((OptionSetValue) result["ldv_preferredcommunicationlanguage"]).Value;
			}
			else
			{
				throw new Exception("Entity support a language spec '" + record.LogicalName + "'.");
			}

			return 1033;
		}

		public static int GetUserTimeZoneBiasMinutes(IOrganizationService service, Guid userId)
		{
			return GetUsersTimeZoneBiasMinutes(service, userId).FirstOrDefault().Value;
		}

		public static IDictionary<Guid, int> GetUsersTimeZoneBiasMinutes(IOrganizationService service, params Guid[] userIds)
		{
			var query = new QueryExpression("usersettings");
			var filter = new FilterExpression(LogicalOperator.Or);

			foreach (var userId in userIds)
			{
				filter.AddCondition("systemuserid", ConditionOperator.Equal, userId);
			}

			query.ColumnSet = new ColumnSet("systemuserid", "timezonebias");
			query.Criteria.Filters.Add(filter);

			var userSettings = service.RetrieveMultiple(query).Entities;

			if (userSettings == null || userSettings.Count < userIds.Length
				|| userSettings.Any(settings => !settings.Contains("timezonebias")))
			{
				throw new Exception("Can't retrieve the settings of the users.");
			}

			return userSettings.ToDictionary(
				settings => settings.GetAttributeValue<Guid>("systemuserid"),
				settings => settings.GetAttributeValue<int>("timezonebias") * -1);
		}

		public static Guid? GetManagerId(IOrganizationService service, Guid userId)
		{
			var user = service.Retrieve("systemuser", userId, new ColumnSet("parentsystemuserid"));

			if (!user.Contains("parentsystemuserid"))
			{
				throw new Exception("User record does not contain a manager.");
			}

			return ((EntityReference) user["parentsystemuserid"]).Id;
		}

		#endregion

		public static void RunWorkflow(IOrganizationService service, Guid entityId, Guid workflowId)
		{
			var request = new ExecuteWorkflowRequest
						  {
							  EntityId = entityId,
							  WorkflowId = workflowId
						  };

			service.Execute(request);
		}

		public static bool IsConditionMet(IOrganizationService service, string fetchXml,
			EntityReference record, bool isActivity = false)
		{
			fetchXml.RequireNotEmpty("fetchXml");

			var primaryIdName = isActivity ? "activityid" : record.LogicalName + "id";

			var finalFetchXml = "";

			var querySplit = fetchXml.Split(new[] {"</entity>"}, StringSplitOptions.None);

			finalFetchXml += querySplit[0];
			finalFetchXml += "<filter type='and'> ";
			finalFetchXml += "<condition attribute='" + primaryIdName + "' operator='eq' value= '" + record.Id + "' /> ";
			finalFetchXml += "</filter>" + "</entity>";
			finalFetchXml += querySplit[querySplit.Length - 1];

			return service.RetrieveMultiple(new FetchExpression(finalFetchXml)).Entities.Any();
		}

		public static string BuildExceptionMessage(Exception ex, string preMessage = null)
		{
			return (preMessage == null ? "" : preMessage + "\r\n") +
				"Exception: " + ex.GetType() +
				"\r\nMessage: " + ex.Message +
				(ex.Source == null ? "" : "\r\nSource: " + ex.Source) +
				(ex.StackTrace == null
					? ""
					: "\r\nStack trace:\r\n" + (ex.StackTrace ?? Helpers.GetStackTrace(-1, "CrmHelpers"))) +
				(ex.InnerException == null
					? ""
					: "\r\nInner exception: " + ex.InnerException.GetType() +
						"\r\nInner message: " + ex.InnerException.Message +
						(ex.InnerException.Source == null ? "" : "\r\nInner source: " + ex.InnerException.Source) +
						(ex.InnerException.StackTrace == null
							? ""
							: "\r\nInner stack trace:\r\n" + ex.InnerException.StackTrace));
		}

		public static InvalidPluginExecutionException BuildInvalidPluginExecException(Exception ex, string preMessage = null)
		{
			preMessage = (preMessage == null ? "" : preMessage + " ") +
				"<div style=\"display:none\">Exception: " + ex.GetType() + " => \"" + ex.Message + "\"." +
				(ex.InnerException == null
					? ""
					: " Inner exception: " + ex.InnerException.GetType() + " => \"" + ex.InnerException.Message + "\".") +
				"</div>";

			var message = "\nException: " + ex.GetType() + " => \"" + ex.Message + "\"." +
				"\nSource: " + ex.Source +
				"\n\n" + (ex.StackTrace ?? Helpers.GetStackTrace(-1, "CrmHelpers")) + "\n" +
				(ex.InnerException == null
					? ""
					: "\n\nInner exception: " + ex.InnerException.GetType() + " => \"" +
						ex.InnerException.Message + "\"." +
						"\nSource: " + ex.InnerException.Source +
						"\n\n" + ex.InnerException.StackTrace + "\n");

			message = message
				.Replace("\"", "&quot;").Replace("'", "\\'").Replace("\n", "<br />").Replace("\\n", "<br />").Replace("\r", "");

			var script = "<br />" +
				"<button class=\"ms-crm-RefreshDialog-Button\" style=\"margin-right: 30px; margin-left: 8px;\"" +
				"onclick=\"" +
				" var w = window.open('', 'Error Details', 'height=100, width=600, toolbar=no, menubar=no, resizable=no, scrollbars=yes, location=no, directories=no, status=no');"
				+
				" parent.$(w.document.body).html('" + message + "');" +
				" w.document.title = 'Error Details';" +
				" parent.$(w.document.body).css({ 'color': 'blue', 'font-size': 13 });" +
				"\">More Details</button>";

			return new InvalidPluginExecutionException(preMessage + script, ex);
		}

		public static void LogAttributeValues(AttributeCollection attributes, Entity labelsRecord, CrmLog log,
			string logLabel = null)
		{
			attributes.Require("attributes");
			labelsRecord.Require("labelsRecord");
			log.Require("log");

			var attributesInfo = new StringBuilder();
			attributesInfo.Append("Attribute values (" + attributes.Count + "):");

			foreach (var attribute in attributes.OrderBy(pair => pair.Key))
			{
				var value = attribute.Value;

				if (value is OptionSetValue)
				{
					value = labelsRecord.FormattedValues.Contains(attribute.Key)
						? labelsRecord.FormattedValues[attribute.Key]
						: value;

					if (value is OptionSetValue)
					{
						value = ((OptionSetValue) value)?.Value;
					}
				}
				else if (value is EntityReference)
				{
					value = (labelsRecord.FormattedValues.Contains(attribute.Key)
						? labelsRecord.FormattedValues[attribute.Key]
						: ((EntityReference) value).Name) ?? value;

					if (value is EntityReference && value != null)
					{
						value = labelsRecord.GetAttributeValue<EntityReference>(attribute.Key)?.Name;
					}
				}
				else if (value is Money)
				{
					value = attribute.Value;
				}

				attributesInfo.Append("\r\n    \"" + attribute.Key + "\" => \"" + value + "\".");
			}

			log.Log(new CrmLog.LogEntry(logLabel ?? "Entity Object Values", LogLevel.Debug, "", "", attributesInfo.ToString()));
		}

		#region Placeholder parsers

		public static T GetFieldValueByPath<T>(IOrganizationService service, EntityReference entityRef, string pathString)
		{
			pathString.RequireNotEmpty("pathString");

			var fieldValue = pathString;
			var path = new Queue<string>();

			foreach (var match in fieldValue.Split('.'))
			{
				path.Enqueue(match);
			}

			if (path.Count <= 0)
			{
				throw new Exception("Poorly formatted path: '" + pathString + "'.");
			}

			var valueRecord = service.Retrieve(entityRef.LogicalName, entityRef.Id, new ColumnSet(path.Peek()));

			while (path.Count > 1)
			{
				var pathNode = path.Dequeue();
				var valueRef = valueRecord.Attributes.FirstOrDefault(pair => pair.Key == pathNode).Value as EntityReference;

				if (valueRef == null)
				{
					return default(T);
				}

				valueRecord = service.Retrieve(valueRef.LogicalName, valueRef.Id, new ColumnSet(path.Peek()));
			}

			var field = path.Peek();
			return service.Retrieve(valueRecord.LogicalName, valueRecord.Id, new ColumnSet(field))
				.GetAttributeValue<T>(field);
		}

		public static string ParseAttributeVariables(IOrganizationService service, string rawString,
			Entity entity, Guid userIdForTimeZone, char separator)
		{
			if (rawString == null)
			{
				return null;
			}

			var stringParsedCond = Regex.Replace(
				rawString, @"{[?](?:(?:{(?:[" + separator + @"]?[a-z_0-9]+[" + separator + @"]?)*?(?:@[^{}]+)?})*?[^{}]*?)*?" +
					@"\:\:(?:(?:{(?:[" + separator + @"]?[a-z_0-9]+[" + separator + @"]?)*?(?:@[^{}]+)?})*?[^{}]*?)*?}",
				match =>
				{
					if (match.Success)
					{
						var condition = match.Value.Split(new[] {"::"}, StringSplitOptions.RemoveEmptyEntries);

						if (condition.Length <= 1)
						{
							return "";
						}

						var filledVal = condition[0].Replace("{?", "");
						// remove the ending '}'
						var emptyVal = condition[1].Substring(0, condition[1].Length - 1);

						filledVal = Regex.Replace(
							filledVal, @"{(?:[" + separator + @"]?[a-z_0-9]+[" + separator + @"]?)*?(?:@[^{}]+)?}",
							match2 =>
							{
								if (match2.Success)
								{
									return $"{{{{{GetFieldValueByPathAsString(service, match2.Value, entity, userIdForTimeZone, separator)}}}}}";
								}

								return "{{}}";
							});

						return filledVal.Contains("{{}}")
							? emptyVal
							: filledVal.Replace("{{", "").Replace("}}", "");
					}

					return "";
				});

			return Regex.Replace(
				stringParsedCond, @"{(?:[" + separator + @"]?[a-z_0-9]+[" + separator + @"]?)*?(?:@[^{}]+)?}",
				match =>
				{
					if (match.Success)
					{
						return GetFieldValueByPathAsString(service, match.Value, entity, userIdForTimeZone, separator) ?? "";
					}

					return "";
				});
		}

		public static string GetFieldValueByPathAsString(IOrganizationService service, string rawVariable,
			Entity entity, Guid userIdForTimeZone, char separator)
		{
			// clean the variable from its delimiters
			var variable = rawVariable.Replace("{", "").Replace("}", "").TrimStart(separator);

			// get all fields in the string
			var field = variable.Split(separator);
			// get the first field to fetch
			var fieldNameRaw = field[0];
			// remove the first field from the string
			variable = variable.Replace(fieldNameRaw, "").TrimStart(separator);
			// get the field name without the date formatter
			var fieldName = fieldNameRaw.Split('@')[0];

			// get the field value from the entity record
			var fieldValue = entity.GetAttributeValue<object>(fieldName);

			// get the entity record
			entity = fieldValue == null
				? service.Retrieve(entity.LogicalName, entity.Id, new ColumnSet(fieldName))
				: entity;

			fieldValue = entity.GetAttributeValue<object>(fieldName);

			if (fieldValue == null)
			{
				return null;
			}

			// variable is recursive, so we need to go deeper through the lookup
			if (!string.IsNullOrEmpty(variable))
			{
				// if the field value is not a lookup, then we can't recurse
				var reference = fieldValue as EntityReference;

				if (reference == null)
				{
					throw new InvalidPluginExecutionException($"Field \"{fieldName}\" is not a lookup.");
				}

				return GetFieldValueByPathAsString(service, variable,
					new Entity(reference.LogicalName)
					{
						Id = reference.Id
					},
					userIdForTimeZone, separator);
			}

			#region Attribute processors

			if (fieldValue is string)
			{
				return (string) fieldValue;
			}

			if (fieldValue is OptionSetValue)
			{
				var label = entity.FormattedValues.FirstOrDefault(keyVal => keyVal.Key == fieldName).Value
					?? GetOptionSetLabel(service, entity.LogicalName, fieldName, (fieldValue as OptionSetValue).Value);
				return label ?? "";
			}

			if (fieldValue is DateTime)
			{
				var dateFormatRaw = fieldNameRaw.Split('@');
				var date = ((DateTime) fieldValue).ConvertToCrmUserTimeZone(service, userIdForTimeZone);
				return dateFormatRaw.Length > 1 ? string.Format("{0:" + dateFormatRaw[1] + "}", date) : date.ToString();
			}

			var fieldRef = fieldValue as EntityReference;

			if (fieldRef != null)
			{
				return fieldRef.Name ?? GetRecordName(service, fieldRef.LogicalName, fieldRef.Id);
			}

			#endregion

			return fieldValue.ToString();
		}

		#endregion
	}

	/// <summary>
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public partial class MetadataHelpers
	{
		#region Metadata helpers

		/// <summary>
		///     Checks whether the given field exists in the entity by its logical name.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static bool IsFieldExistInEntity(IOrganizationService service, string entityName, string fieldName)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.AddRange("Attributes");

			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions.Add(
				new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals,
					entityName));

			var attributeProperties = new MetadataPropertiesExpression
									  {
										  AllProperties = false
									  };
			attributeProperties.PropertyNames.AddRange("LogicalName");

			var attributeFilter = new MetadataFilterExpression(LogicalOperator.And);
			attributeFilter.Conditions.Add(
				new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals,
					fieldName));

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties,
											AttributeQuery = new AttributeQueryExpression
															 {
																 Criteria = attributeFilter,
																 Properties = attributeProperties
															 }
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			var result = ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest)).EntityMetadata;
			
			return result != null && result.Count > 0 && result.First().Attributes.Length > 0
				&& result.First().Attributes.Any(attribute => attribute.LogicalName == fieldName);
		}

		public static string[] NonStandard = {
												 "applicationfile"
												 , "attachment" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "authorizationserver" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "businessprocessflowinstance"
												 // Not included with CrmSvcUtil 2013  http://community.dynamics.com/crm/f/117/t/117642.aspx
												 , "businessunitmap" // Not included with CrmSvcUtil 2013
												 , "clientupdate" // Not included with CrmSvcUtil 2013
												 , "commitment" // Not included with CrmSvcUtil 2013
												 , "competitoraddress"
												 //isn't include in CrmSvcUtil but it shows in the default solution
												 , "complexcontrol" //Not Included with CrmSvcUtil 2013
												 , "dependencynode" //Not Included with CrmSvcUtil 2013
												 , "displaystringmap" // Not Included with CrmSvcUtil 2013
												 , "documentindex" // Not Included with CrmSvcUtil 2013
												 , "emailhash" // Not Included with CrmSvcUtil 2013
												 , "emailsearch" // Not Included with CrmSvcUtil 2013
												 , "filtertemplate" // Not Included with CrmSvcUtil 2013
												 , "imagedescriptor" // Not included with CrmSvcUtil 2013
												 , "importdata" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "integrationstatus" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "interprocesslock" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "multientitysearchentities" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "multientitysearch" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "notification" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "organizationstatistic" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "owner" // Not included with CrmSvcUtil 2013
												 , "partnerapplication" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "principalattributeaccessmap" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "principalobjectaccessreadsnapshot"
												 // Not included with CrmSvcUtil 6.0.0001.0061
												 , "principalobjectaccess" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "privilegeobjecttypecodes" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "postregarding" // Not included with CrmSvcUtil 2013
												 , "postrole" // Not included with CrmSvcUtil 2013
												 , "subscriptionclients" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "salesprocessinstance" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "recordcountsnapshot" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "replicationbacklog" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "resourcegroupexpansion" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "ribboncommand" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "ribboncontextgroup" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "ribbondiff" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "ribbonrule" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "ribbontabtocommandmap" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "roletemplate" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "statusmap" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "stringmap" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "sqlencryptionaudit"
												 , "subscriptionsyncinfo"
												 , "subscription" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "subscriptiontrackingdeletedobject"
												 , "systemapplicationmetadata" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "systemuserbusinessunitentitymap"
												 // Not included with CrmSvcUtil 6.0.0001.0061
												 , "systemuserprincipals" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "traceassociation" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "traceregarding" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "unresolvedaddress" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "userapplicationmetadata" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "userfiscalcalendar" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "webwizard" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "wizardaccessprivilege" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "wizardpage" // Not included with CrmSvcUtil 6.0.0001.0061
												 , "workflowwaitsubscription" // Not included with CrmSvcUtil 6.0.0001.0061
												 // the following cause duplicate errors in generated code
												 , "bulkdeleteoperation"
												 , "reportlink"
												 , "rollupjob"
											 };

		/// <summary>
		///     Get the names of all entities.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static List<EntityMetadata> GetEntities(IOrganizationService service,
			params CrmHelpers.EntityAttribute[] attributes)
		{
			return GetEntities(service, attributes.Select(attribute => attributes.ToString()).ToArray());
		}

		/// <summary>
		///     Get the names of all entities.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static List<EntityMetadata> GetEntities(IOrganizationService service, params string[] attributes)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };

			if (attributes != null && attributes.Any())
			{
				entityProperties.PropertyNames.AddRange(attributes);
			}

			var entityQueryExpression = new EntityQueryExpression
										{
											Properties = entityProperties
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			return ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest)).EntityMetadata
				.Where(entity =>
					   {
						   if (entity.SchemaName == null || entity.LogicalName == null)
						   {
							   return false;
						   }

						   return !NonStandard.Contains(entity.LogicalName);
					   }).ToList();
		}

		/// <summary>
		///     Get the value of an entity attribute from the metadata.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static T GetEntityAttribute<T>(IOrganizationService service, string entityName,
			CrmHelpers.EntityAttribute attribute)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.AddRange(attribute.ToString());

			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions.Add(
				new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals,
					entityName));

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			var metadata = ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest))
				.EntityMetadata?.FirstOrDefault();
			return (T) metadata?.GetType().GetProperty(attribute.ToString())?.GetValue(metadata);
		}

		/// <summary>
		///     Gets the logical name using the entity's object type code.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string GetEntityNameUsingTypeCode(IOrganizationService service, int typeCode)
		{
			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions.Add(new MetadataConditionExpression("ObjectTypeCode",
				MetadataConditionOperator.Equals, typeCode));

			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.AddRange("LogicalName");

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			return ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest))
				.EntityMetadata?.FirstOrDefault()?.LogicalName;
		}

		/// <summary>
		///     Get the value of a field attribute from the metadata.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static T GetFieldAttribute<T>(IOrganizationService service, string entityName, string fieldName,
			CrmHelpers.FieldAttribute attribute)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.AddRange("Attributes");

			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions.Add(
				new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals, entityName));

			var attributeFilter = new MetadataFilterExpression(LogicalOperator.And);
			attributeFilter.Conditions
				.Add(new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals, fieldName));

			var attributeProperties = new MetadataPropertiesExpression
									  {
										  AllProperties = false
									  };
			attributeProperties.PropertyNames.AddRange(attribute.ToString());

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties,
											AttributeQuery = new AttributeQueryExpression
															 {
																 Properties = attributeProperties,
																 Criteria = attributeFilter
															 }
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			var metadata = ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest))
				.EntityMetadata?.FirstOrDefault();

			AttributeMetadata fieldmetadata = null;

			if (metadata != null)
			{
				fieldmetadata = metadata.Attributes?.FirstOrDefault();
			}

			return (T) fieldmetadata?.GetType().GetProperty(attribute.ToString())?.GetValue(fieldmetadata);
		}

		/// <summary>
		///     Get the value of a relation attribute from the metadata.<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static T GetRelationAttribute<T>(IOrganizationService service, string entityName, string relationName,
			CrmHelpers.RelationType type, CrmHelpers.RelationAttribute attribute)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.Add(type.ToString());

			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions
				.Add(new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals, entityName));

			var relationFilter = new MetadataFilterExpression(LogicalOperator.And);
			relationFilter.Conditions
				.Add(new MetadataConditionExpression("SchemaName", MetadataConditionOperator.Equals, relationName));


			var relationProperties = new MetadataPropertiesExpression
									 {
										 AllProperties = false
									 };
			relationProperties.PropertyNames.AddRange(attribute.ToString());

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties,
											RelationshipQuery = new RelationshipQueryExpression
																{
																	Properties = relationProperties,
																	Criteria = relationFilter
																}
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			var metadata = ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest))
				.EntityMetadata?.FirstOrDefault();

			if (metadata == null)
			{
				return default(T);
			}

			RelationshipMetadataBase relationMetadata = null;

			if (metadata.OneToManyRelationships != null)
			{
				relationMetadata = metadata.OneToManyRelationships.FirstOrDefault();
			}

			if (metadata.ManyToOneRelationships != null)
			{
				relationMetadata = metadata.ManyToOneRelationships.FirstOrDefault();
			}

			if (metadata.ManyToManyRelationships != null)
			{
				relationMetadata = metadata.ManyToManyRelationships.FirstOrDefault();
			}

			return (T) relationMetadata?.GetType().GetProperty(attribute.ToString())?.GetValue(relationMetadata);
		}

		public static List<RelationshipMetadataBase> GetCustomRelationships(IOrganizationService service,
			string entityName, CrmHelpers.RelationType[] types, CrmHelpers.RelationAttribute[] attributes)
		{
			var entityProperties = new MetadataPropertiesExpression
								   {
									   AllProperties = false
								   };
			entityProperties.PropertyNames.AddRange(types.Select(type => type.ToString()));

			var entityFilter = new MetadataFilterExpression(LogicalOperator.And);
			entityFilter.Conditions.Add(
				new MetadataConditionExpression("LogicalName", MetadataConditionOperator.Equals, entityName));

			var relationFilter = new MetadataFilterExpression(LogicalOperator.And);
			relationFilter.Conditions
				.Add(new MetadataConditionExpression("IsCustomRelationship", MetadataConditionOperator.Equals, true));

			var relationProperties = new MetadataPropertiesExpression
									 {
										 AllProperties = false
									 };
			relationProperties.PropertyNames.AddRange(attributes.Select(attribute => attribute.ToString()));
			if (!attributes.Contains(CrmHelpers.RelationAttribute.RelationshipType))
			{
				relationProperties.PropertyNames.AddRange(CrmHelpers.RelationAttribute.RelationshipType.ToString());
			}

			var entityQueryExpression = new EntityQueryExpression
										{
											Criteria = entityFilter,
											Properties = entityProperties,
											RelationshipQuery = new RelationshipQueryExpression
																{
																	Properties = relationProperties,
																	Criteria = relationFilter
																}
										};

			var retrieveMetadataChangesRequest = new RetrieveMetadataChangesRequest
												 {
													 Query = entityQueryExpression,
													 ClientVersionStamp = null
												 };

			var metadata = ((RetrieveMetadataChangesResponse) service.Execute(retrieveMetadataChangesRequest))
				.EntityMetadata?.FirstOrDefault();

			var relationMetadata = new List<RelationshipMetadataBase>();

			if (metadata == null)
			{
				return relationMetadata;
			}

			if (metadata.OneToManyRelationships != null)
			{
				relationMetadata.AddRange(metadata.OneToManyRelationships);
			}

			if (metadata.ManyToOneRelationships != null)
			{
				relationMetadata.AddRange(metadata.ManyToOneRelationships);
			}

			if (metadata.ManyToManyRelationships != null)
			{
				relationMetadata.AddRange(metadata.ManyToManyRelationships);
			}

			return relationMetadata;
		}

		public static string GetOptionSetLabel(IOrganizationService service, string entityName, string fieldName,
			int value)
		{
			return
				GetFieldAttribute<OptionSetMetadata>(service, entityName, fieldName, CrmHelpers.FieldAttribute.OptionSet)?
					.Options.FirstOrDefault(optionQ => optionQ.Value == value)?
					.Label?.UserLocalizedLabel?.Label;
		}

		#endregion
	}

	/// <summary>
	///     Unlike <see cref="Semaphore" />, this implementation ensures that holds are released in the order of acquiring the
	///     hold.
	///     It also implements <see cref="IDisposable" />, but in a reusable fashion; e.g:
	///     <code>	using (fifoSemaphore.GetPermit())
	/// 	{
	///  		// code ...
	///   	}
	///     </code>
	///     After exiting the 'using' block, <see cref="ReleasePermit" /> is called automatically.<br />
	///     Author: Ahmed el-Sawalhy (LINK Development)
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public sealed class FifoSemaphore : IDisposable
	{
		private readonly Queue<ManualResetEvent> threadLocksQueue = new Queue<ManualResetEvent>();

		private readonly int maxConcurrency;
		private int currentRequests;

		private readonly object lockObject = new object();

		public FifoSemaphore(int maxConcurrency)
		{
			this.maxConcurrency = maxConcurrency;
		}

		/// <summary>
		///     Check how many permits have been requested before, and if the number is greater than the limit,
		///     hold this request until a permit elsewhere is released.
		/// </summary>
		public FifoSemaphore GetPermit()
		{
			ManualResetEvent newLock;

			lock (lockObject)
			{
				currentRequests++;

				// if the limit hasn't been reached yet, note it, and give permission
				if (currentRequests <= maxConcurrency)
				{
					return this;
				}

				// we have to wait for a slot to open
				newLock = new ManualResetEvent(false);
				threadLocksQueue.Enqueue(newLock);
			}

			newLock.WaitOne();

			return this;
		}

		/// <summary>
		///     Release a permit, and release the hold on the next in line.
		/// </summary>
		public void ReleasePermit()
		{
			lock (lockObject)
			{
				// note the release
				currentRequests--;

				// give permission to the next in line
				if (threadLocksQueue.Any())
				{
					threadLocksQueue.Dequeue().Set();
				}
			}
		}

		public void Dispose()
		{
			ReleasePermit();
		}
	}

	/// <summary>
	///     credits: http://joe-bq-wang.iteye.com/blog/1878940 <br />
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public sealed class QueuedLock : IDisposable
	{
		private readonly object innerLock;
		private volatile int ticketsCount;
		private volatile int ticketToRide;

		public QueuedLock()
		{
			innerLock = new object();
			ticketToRide = ticketsCount + 1;
		}

		public QueuedLock Enter()
		{
			var myTicket = Interlocked.Increment(ref ticketsCount);
			Monitor.Enter(innerLock);

			while (true)
			{
				if (myTicket == ticketToRide)
				{
					return this;
				}

				Monitor.Wait(innerLock);
			}
		}

		public void Exit()
		{
			Interlocked.Increment(ref ticketToRide);
			Monitor.PulseAll(innerLock);
			Monitor.Exit(innerLock);
		}

		public void Dispose()
		{
			Exit();
		}
	}

	/// <summary>
	///     credits: http://joe-bq-wang.iteye.com/blog/1878940
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public class BlockingQueue<T> : BlockingCollection<T>
	{
		#region ctor(s)

		public BlockingQueue()
			: base(new ConcurrentQueue<T>())
		{ }

		public BlockingQueue(int maxSize)
			: base(new ConcurrentQueue<T>(), maxSize)
		{ }

		#endregion ctor(s)

		#region Methods

		/// <summary>
		///     Enqueue an Item
		/// </summary>
		/// <param name="item">Item to enqueue</param>
		/// <remarks>blocks if the blocking queue is full</remarks>
		public void Enqueue(T item)
		{
			Add(item);
		}

		/// <summary>
		///     Dequeue an item
		/// </summary>
		/// <returns>Item dequeued</returns>
		/// <remarks>blocks if the blocking queue is empty</remarks>
		public T Dequeue()
		{
			return Take();
		}

		/// <summary>
		///     Clears the queue of all items
		/// </summary>
		public void Clear()
		{
			while (this.Any())
			{
				Dequeue();
			}
		}

		#endregion Methods
	}

	/// <summary>
	///     credit: http://stackoverflow.com/a/961904/1919456 <br />
	///     Credit: http://stackoverflow.com/questions/19049514/strategy-for-logging-in-production-for-dynamics-crm-plugins
	///     <br />
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	internal static class PluginInfo
	{
		public static string GetPluginExecutionInfo(IOrganizationService organizationService, IExecutionContext context)
		{
			try
			{
				var lines = new List<string>();

				Entity target = null;
				if (context.InputParameters.ContainsKey("Target"))
				{
					target = context.InputParameters["Target"] as Entity;

					if (target == null)
					{
						var tempTarget = (EntityReference) context.InputParameters["Target"];
						target = organizationService.Retrieve(tempTarget.LogicalName, tempTarget.Id, new ColumnSet(true));
					}
				}

				lines.Add("MessageName: " + context.MessageName);
				lines.Add("PrimaryEntityName: " + context.PrimaryEntityName);
				lines.Add("PrimaryEntityId: " + context.PrimaryEntityId);
				lines.Add("BusinessUnitId: " + context.BusinessUnitId);
				lines.Add("CorrelationId: " + context.CorrelationId);
				lines.Add("Depth: " + context.Depth);

				var contextTemp = context as IPluginExecutionContext;

				if (contextTemp != null)
				{
					lines.Add("Has Parent Context: " + (contextTemp.ParentContext != null));
				}

				lines.Add("InitiatingUserId: " + context.InitiatingUserId);
				AddParameters(lines, context.InputParameters, "Input Parameters");
				lines.Add("IsInTransaction: " + context.IsInTransaction);
				lines.Add("IsolationMode: " + context.IsolationMode);
				lines.Add("Mode: " + context.Mode);
				lines.Add("OperationCreatedOn: " + context.OperationCreatedOn);
				lines.Add("OperationId: " + context.OperationId);
				lines.Add("Organization: " + context.OrganizationName + "(" + context.OrganizationId + ")");
				AddParameters(lines, context.OutputParameters, "Output Parameters");
				AddEntityReference(lines, context.OwningExtension, "OwningExtension");
				AddEntityImages(organizationService, lines, context.PostEntityImages, "Post Entity Images");
				AddEntityImages(organizationService, lines, context.PreEntityImages, "Pre Entity Images");
				lines.Add("SecondaryEntityName: " + context.SecondaryEntityName);
				AddParameters(lines, context.SharedVariables, "Shared Variables");

				if (contextTemp != null)
				{
					lines.Add("Stage: " + contextTemp.Stage);
				}

				lines.Add("UserId: " + context.UserId);

				if (target == null || target.Attributes.Count == 0)
				{
					lines.Add("Target: Empty ");
				}
				else
				{
					lines.Add("* Target " + target.ToEntityReference().Name + " *");
					lines.AddRange(
						target.Attributes.Select(
							att =>
								"    Entity[" + att.Key + "]: " +
									GetAttributeValue(organizationService, target.LogicalName, att.Key, att.Value)));
				}

				foreach (
					var entity in
						context.InputParameters.Where(param => param.Key != "Target").Select(param => param.Value).OfType<Entity>())
				{
					lines.Add("* Entity " + entity.ToEntityReference().Name + " *");
					lines.AddRange(
						entity.Attributes.Select(
							att =>
								"    Entity[" + att.Key + "]: " +
									GetAttributeValue(organizationService, entity.LogicalName, att.Key, att.Value)));
				}

				return string.Join(Environment.NewLine, lines);
			}
			catch
			{
				return "";
			}
		}

		private static string GetAttributeValue(IOrganizationService organizationService, string logicalName, string key,
			object value)
		{
			if (value == null)
			{
				return "Null";
			}

			var type = value.GetType();

			if (type == typeof(OptionSetValue))
			{
				var retrieveOptionSetRequest = new RetrieveAttributeRequest
											   {
												   EntityLogicalName = logicalName,
												   LogicalName = key,
												   RetrieveAsIfPublished = true
											   };

				var response = (RetrieveAttributeResponse) organizationService.Execute(retrieveOptionSetRequest);

				var metadata = response.AttributeMetadata as EnumAttributeMetadata;

				if (metadata != null)
				{
					var valueTemp = ((OptionSetValue) value).Value;
					return valueTemp + " (" +
						metadata.OptionSet.Options.First(option => option.Value == valueTemp).Label.UserLocalizedLabel.Label + ")";
				}
			}

			if (type != typeof(EntityReference))
			{
				return value.ToString();
			}

			var reference = (EntityReference) value;

			if (reference.LogicalName == null)
			{
				return value.ToString();
			}

			var primaryAttribute = ((RetrieveEntityResponse) organizationService.Execute(new RetrieveEntityRequest
																						 {
																							 EntityFilters = EntityFilters.Entity,
																							 LogicalName = reference.LogicalName
																						 })).EntityMetadata.PrimaryNameAttribute;

			return reference.Id + " (" +
				organizationService.Retrieve(reference.LogicalName, reference.Id, new ColumnSet(primaryAttribute))
					.GetAttributeValue<string>(primaryAttribute) + ")";
		}

		private static void AddEntityReference(ICollection<string> nameValuePairs, EntityReference entity, string name)
		{
			if (entity != null)
			{
				nameValuePairs.Add(name + ": " + entity.Name);
			}
		}

		private static void AddEntityImages(IOrganizationService organizationService, List<string> nameValuePairs,
			EntityImageCollection images, string name)
		{
			if (images != null && images.Count > 0)
			{
				nameValuePairs.Add("** " + name + " **");
				foreach (var image in images)
				{
					if (image.Value == null || image.Value.Attributes.Count == 0)
					{
						if (image.Value != null)
						{
							nameValuePairs.Add("    Image[" + image.Key + "] " + image.Value.ToEntityReference().Name + ": Empty");
						}
					}
					else
					{
						nameValuePairs.Add("*   Image[" + image.Key + "] " + image.Value.ToEntityReference().Name + "   *");
						nameValuePairs.AddRange(
							image.Value.Attributes.Select(
								att =>
									"        Entity[" + att.Key + "]: " +
										GetAttributeValue(organizationService, image.Value.ToEntityReference().LogicalName, att.Key, att.Value)));
					}
				}
			}
			else
			{
				nameValuePairs.Add(name + ": Empty");
			}
		}

		private static void AddParameters(List<string> nameValuePairs, ParameterCollection parameters, string name)
		{
			if (parameters != null && parameters.Count > 0)
			{
				nameValuePairs.Add("* " + name + " *");
				nameValuePairs.AddRange(parameters.Select(param => "    Param[" + param.Key + "]: " + param.Value));
			}
			else
			{
				nameValuePairs.Add(name + ": Empty");
			}
		}
	}

	#endregion

	#region Extensions

	/// <summary>
	///     Author: Ahmed el-Sawalhy
	/// </summary>
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public static partial class Extensions
	{
		#region Param checks

		// Credit: http://www.codeproject.com/Articles/290695/Extension-methods-to-simplify-null-argument-check

		/// <summary>
		///     defines default exception message for string type, if not specified .
		/// </summary>
		private const string STRING_EXCEPTION_MSG = "String value cannot be empty.";

		/// <summary>
		///     defines default exception message for string format, if not specified.
		/// </summary>
		private const string DEFAULT_STRING_FORMAT_EXCEPTION_MSG = "String format is invalid.";

		/// <summary>
		///     defines default message for null Object and Nullable type
		/// </summary>
		private const string DEFAULT_NULL_EXCEPTION_MESSAGE = "Value cannot be null.";

		private const string DEFAULT_RANGE_MESSAGE = "Parameter value out of range.";

		private const string DEFAULT_ARRAY_MEMBER_MESSAGE = "Array member can't be null.";
		private const string DEFAULT_ENUMERABLE_COUNT_MESSAGE = "Enumerable count is out of range.";

		/// <summary>
		///     defines default parameter name for all types
		/// </summary>
		private const string DEFAULT_PARAMETER_NAME = "Unknown";

		#region NULL CHECK FOR OBJECT OF ANY CLASS

		/// <summary>
		///     Generic extension method that throws ArgumentNullException if target object is null.
		///     The method is constrained to objects of class type. The method is intended to be used
		///     for null parameter check.
		/// </summary>
		/// <typeparam name="T">Type of parameter</typeparam>
		/// <param name="obj">Target object of type T</param>
		/// <param name="paramName">
		///     Name of the parameter.If paramName name is null empty or whitespace default value will be
		///     paramNmae = "Unknown".
		/// </param>
		/// <param name="message">Exception message.If not provided default value is "Value can not be null."</param>
		public static void Require<T>(this T obj, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_NULL_EXCEPTION_MESSAGE) where T : class
		{
			if (obj == null)
			{
				ThrowArgumentNullException(ref paramName, ref message);
			}
		}

		#endregion NULL CHECK FOR OBJECT OF ANY CLASS

		#region NULL CHECK FOR OBJECT OF NULLABLE TYPE

		/// <summary>
		///     Generic extension method that throws ArgumentNullException if type value is null.
		///     The method is constrained to objects of Nullable struct type. The method is intended to be used
		///     for null parameter check.
		/// </summary>
		/// <typeparam name="T">Type of target object</typeparam>
		/// <param name="obj">Target object of type T.</param>
		/// <param name="paramName">
		///     Name of the parameter.if pramName is null empty or whitespace default paramName = "Unknown"
		///     will be used.
		/// </param>
		/// <param name="message">Exception message.If not provoided default value is "Value can not be null."</param>
		public static void Require<T>(this T? obj, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_NULL_EXCEPTION_MESSAGE) where T : struct
		{
			if (obj == null)
			{
				ThrowArgumentNullException(ref paramName, ref message);
			}
		}

		#endregion NULL CHECK FOR OBJECT OF NULLABLE TYPE

		#region NULL CHECK FOR OBJECT ARRAY

		/// <summary>
		///     Method throws ArgumentNullException if any of object reference in array is null.
		///     Note : Using value types in array may result in unnecessary boxing.
		///     Use only when you do not care about message , parameter name and empty/whitespace string.
		///     Certainly not enough for production quality code.
		/// </summary>
		/// <param name="objects">Object array containing target object references.</param>
		/// <param name="paramName"></param>
		/// <param name="message"></param>
		public static void Require(this object[] objects, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_ARRAY_MEMBER_MESSAGE)
		{
			if (objects == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (objects.Any(param => param == null))
			{
				ThrowArgumentNullException(ref paramName, ref message);
			}
		}

		public static void RequireCountBelow<T>(this T collection, int max, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : ICollection
		{
			if (collection == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (collection.Count >= max)
			{
				message = string.Format(@"Collection count must be less than ""{0}"".", max);
				ThrowArgumentOutOfRangeException(ref paramName, collection, ref message, collection.Count);
			}
		}

		public static void RequireCountAtMost<T>(this T collection, int max, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : ICollection
		{
			if (collection == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (collection.Count > max)
			{
				message = string.Format(@"Collection count must be less than or equal to ""{0}"".", max);
				ThrowArgumentOutOfRangeException(ref paramName, collection, ref message, collection.Count);
			}
		}

		public static void RequireCountAbove<T>(this T collection, int min, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : ICollection
		{
			if (collection == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (collection.Count <= min)
			{
				message = string.Format(@"Collection count must be greater than ""{0}"".", min);
				ThrowArgumentOutOfRangeException(ref paramName, collection, ref message, collection.Count);
			}
		}

		public static void RequireCountAtLeast<T>(this T collection, int min, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : ICollection
		{
			if (collection == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (collection.Count < min)
			{
				message = string.Format(@"Collection count must be greater than or equal to ""{0}"".", min);
				ThrowArgumentOutOfRangeException(ref paramName, collection, ref message, collection.Count);
			}
		}

		public static void RequireCountInRange<T>(this T collection, int min, int max,
			string paramName = DEFAULT_PARAMETER_NAME, string message = DEFAULT_RANGE_MESSAGE) where T : ICollection
		{
			if (collection == null)
			{
				var messageTemp = string.Intern(DEFAULT_NULL_EXCEPTION_MESSAGE);
				ThrowArgumentNullException(ref paramName, ref messageTemp);
				return;
			}

			if (collection.Count > max && collection.Count < min)
			{
				message = string.Format(@"Collection count must be between ""{0}"" and ""{1}"" inclusive.", min, max);
				ThrowArgumentOutOfRangeException(ref paramName, collection, ref message, collection.Count);
			}
		}

		#endregion NULL CHECK FOR OBJECT ARRAY

		#region Null, format, and empty check for strings

		/// <summary>
		///     Extension method that throws ArgumentNullException if target string is null , empty or whitespace.
		///     The method is constrained to objects of string type. The method is intended to be used
		///     for null parameter check.
		/// </summary>
		/// <param name="stringObj">Target string object</param>
		/// <param name="paramName">
		///     Name of the parameter.if pramName is null empty or whitespace default paramName = "Unknown"
		///     will be used.
		/// </param>
		/// <param name="message">
		///     Exception message.If not provided default value is "String value can not be null , empty of white
		///     space."
		/// </param>
		public static void Require(this string stringObj, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_NULL_EXCEPTION_MESSAGE)
		{
			if (stringObj == null)
			{
				ThrowArgumentNullException(ref paramName, ref message);
			}
		}

		public static void RequireNotEmpty(this string stringObj, string paramName = DEFAULT_PARAMETER_NAME,
			string message = STRING_EXCEPTION_MSG)
		{
			stringObj.Require(paramName);

			if (string.IsNullOrEmpty(stringObj))
			{
				ThrowArgumentNullException(ref paramName, ref message);
			}
		}

		public static void RequireFormat(this string stringObj, string regex, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_STRING_FORMAT_EXCEPTION_MSG)
		{
			stringObj.Require(paramName);

			if (Regex.IsMatch(stringObj, regex))
			{
				ThrowArgumentFormatException(ref message, ref paramName, ref regex);
			}
		}

		#endregion

		#region Range check for comparables

		// Author: Ahmed el-Sawalhy

		public static void RequireBelow<T>(this T number, T max, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : IComparable
		{
			if (number.CompareTo(max) >= 0)
			{
				message = string.Format(@"Parameter must be less than ""{0}"".", max);
				ThrowArgumentOutOfRangeException(ref paramName, number, ref message);
			}
		}

		public static void RequireAtMost<T>(this T number, T max, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : IComparable
		{
			if (number.CompareTo(max) > 0)
			{
				message = string.Format(@"Parameter must be less than or equal to ""{0}"".", max);
				ThrowArgumentOutOfRangeException(ref paramName, number, ref message);
			}
		}

		public static void RequireAbove<T>(this T number, T min, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : IComparable
		{
			if (number.CompareTo(min) <= 0)
			{
				message = string.Format(@"Parameter must be greater than ""{0}"".", min);
				ThrowArgumentOutOfRangeException(ref paramName, number, ref message);
			}
		}

		public static void RequireAtLeast<T>(this T number, T min, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : IComparable
		{
			if (number.CompareTo(min) < 0)
			{
				message = string.Format(@"Parameter must be greater than or equal to ""{0}"".", min);
				ThrowArgumentOutOfRangeException(ref paramName, number, ref message);
			}
		}

		public static void RequireInRange<T>(this T number, T min, T max, string paramName = DEFAULT_PARAMETER_NAME,
			string message = DEFAULT_RANGE_MESSAGE) where T : IComparable
		{
			if (number.CompareTo(min) < 0 || number.CompareTo(max) > 0)
			{
				message = string.Format(@"Parameter must be between ""{0}"" and ""{1}"" inclusive.", min, max);
				ThrowArgumentOutOfRangeException(ref paramName, number, ref message);
			}
		}

		#endregion

		#region METHOD THAT ACTUALLY THROWS EXCEPTION

		private static void ThrowArgumentNullException(ref string paramName, ref string message)
		{
			throw new ArgumentNullException(paramName, message);
		}

		private static void ThrowArgumentOutOfRangeException(ref string paramName, object number, ref string message,
			int currentCount = -1)
		{
			throw new ArgumentOutOfRangeException(paramName, number,
				message + ((currentCount >= 0) ? " Current size is " + currentCount : ""));
		}

		private static void ThrowArgumentFormatException(ref string paramName, ref string message, ref string regex)
		{
			throw new ArgumentException(message + " Format: " + regex, paramName);
		}

		#endregion METHOD THAT ACTUALLY THROWS EXCEPTION

		#endregion

		#region Strings

		/// <summary>
		///     Credit: http://stackoverflow.com/a/6724896/1919456 <br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string Truncate(this string value, int maxChars, string replacement)
		{
			return value.Length <= maxChars ? value : value.Substring(0, maxChars) + replacement;
		}

		public static string ToTitleCase(this string str)
		{
			return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(str.ToLower());
		}

		private const string ms_regexEscapes = @"[\a\b\f\n\r\t\v\\""]";

		/// <summary>
		///     Credit: http://stackoverflow.com/a/323670/1919456 <br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string StringLiteral(this string i_string)
		{
			var replaceDict = new Dictionary<string, string>();
			replaceDict["\a"] = @"\a";
			replaceDict["\b"] = @"\b";
			replaceDict["\f"] = @"\f";
			replaceDict["\n"] = @"\n";
			replaceDict["\r"] = @"\r";
			replaceDict["\t"] = @"\t";
			replaceDict["\v"] = @"\v";
			replaceDict["\\"] = @"\\";
			replaceDict["\0"] = @"\0";
			replaceDict["\""] = @"\""";

			return Regex.Replace(i_string, ms_regexEscapes,
				m =>
				{
					var match = replaceDict.FirstOrDefault(s => s.Key == m.ToString());
					return match.Value ?? m.ToString();
				});
		}

		/// <summary>
		///     Credit: http://stackoverflow.com/a/323670/1919456 <br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		public static string StringUnLiteral(this string i_string)
		{
			var replaceDict = new Dictionary<string, string>();
			replaceDict[@"\a"] = "\a";
			replaceDict[@"\b"] = "\b";
			replaceDict[@"\f"] = "\f";
			replaceDict[@"\n"] = "\n";
			replaceDict[@"\r"] = "\r";
			replaceDict[@"\t"] = "\t";
			replaceDict[@"\v"] = "\v";
			replaceDict[@"\\"] = "\\";
			replaceDict[@"\0"] = "\0";
			replaceDict[@"\"""] = "\"";

			return Regex.Replace(i_string, ms_regexEscapes,
				m =>
				{
					var match = replaceDict.FirstOrDefault(s => s.Key == m.ToString());
					return match.Value ?? m.ToString();
				});
		}

		/// <summary>
		///     Compresses a string and returns a deflate compressed, Base64 encoded string.<br />
		///     Credit: https://stackoverflow.com/a/43357353/1919456<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="uncompressedString">String to compress</param>
		public static string Compress(this string uncompressedString)
		{
			using (var compressedStream = new MemoryStream())
			{
				using (var uncompressedStream = new MemoryStream(Encoding.UTF8.GetBytes(uncompressedString)))
				{
					using (var compressorStream = new DeflateStream(compressedStream, CompressionLevel.Optimal, true))
					{
						uncompressedStream.CopyTo(compressorStream);
					}
				}

				return Convert.ToBase64String(compressedStream.ToArray());
			}
		}

		/// <summary>
		///     Decompresses a deflate compressed, Base64 encoded string and returns an uncompressed string.<br />
		///     Credit: https://stackoverflow.com/a/43357353/1919456<br />
		///     Author: Ahmed el-Sawalhy
		/// </summary>
		/// <param name="compressedString">String to decompress.</param>
		public static string Decompress(this string compressedString)
		{
			using (var decompressedStream = new MemoryStream())
			{
				using (var compressedStream = new MemoryStream(Convert.FromBase64String(compressedString)))
				{
					using (var decompressorStream = new DeflateStream(compressedStream, CompressionMode.Decompress))
					{
						decompressorStream.CopyTo(decompressedStream);
						return Encoding.UTF8.GetString(decompressedStream.ToArray());
					}
				}
			}
		}

		public static string CharLiteral(this char c)
		{
			return c == '\'' ? @"'\''" : string.Format("'{0}'", c);
		}

		#endregion

		public static DateTime ConvertToCrmUserTimeZone(this DateTime dateTime, IOrganizationService service, Guid userId)
		{
			var bias = CrmHelpers.GetUserTimeZoneBiasMinutes(service, userId);
			var biasedDate = dateTime.AddMinutes(bias);

			return DateTime.SpecifyKind(biasedDate, DateTimeKind.Local);
		}

		public static DateTime ConvertToCrmUtcTimeZone(this DateTime dateTime, IOrganizationService service, Guid userId)
		{
			var bias = CrmHelpers.GetUserTimeZoneBiasMinutes(service, userId);
			var biasedDate = dateTime.AddMinutes(-bias);

			return DateTime.SpecifyKind(biasedDate, DateTimeKind.Utc);
		}

		public static DateTime ConvertBetweenCrmUsersTimeZone(this DateTime dateTime, IOrganizationService service,
			Guid user1Id, Guid user2Id)
		{
			var biases = CrmHelpers.GetUsersTimeZoneBiasMinutes(service, user1Id, user2Id);
			var bias1 = biases[user1Id];
			var bias2 = biases[user2Id];

			var biasedDate = dateTime.AddMinutes(-bias1).AddMinutes(bias2);

			return DateTime.SpecifyKind(biasedDate, DateTimeKind.Local);
		}
	}

	#endregion

	// -----------------------------------------------------------------------------------------------------------------------------------------------------------------
	//
	// Copyright (c) 2016 Bas van de Sande - JourneyIntoCRM - http://journeyintocrm.com 
	//
	// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), 
	// to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
	// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
	//
	// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
	//
	// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
	// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
	// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
	//
	// -----------------------------------------------------------------------------------------------------------------------------------------------------------------
	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public static class EntitySerializer
	{
		public static string SerializeObject(Entity entity)
		{
			var ds = new DataSet("XmlData");
			var dt = new DataTable(entity.LogicalName);
			ConvertEntityToDataTable(dt, entity);
			ds.Tables.Add(dt);
			return ds.GetXml();
		}

		private static void ConvertEntityToDataTable(DataTable dataTable, Entity entity)
		{
			var row = dataTable.NewRow();
			dataTable.Columns.Add("Id");
			row["Id"] = GetAttributeValue(entity.Id);
			foreach (var attribute in entity.Attributes.Where(a => a.Value != null))
			{
				if (!dataTable.Columns.Contains(attribute.Key))
				{
					dataTable.Columns.Add(attribute.Key);
				}
				row[attribute.Key] = GetAttributeValue(attribute.Value);
			}
			foreach (var fv in entity.FormattedValues)
			{
				if (!dataTable.Columns.Contains(fv.Key + "name"))
				{
					dataTable.Columns.Add(fv.Key + "name");
				}
				row[fv.Key + "name"] = fv.Value;
			}
			dataTable.Rows.Add(row);
		}

		private static string GetAttributeValue(object entityValue)
		{
			string objectValue;
			var objectReference = "";
			var objectType = $"{entityValue.GetType()}";

			switch (entityValue.ToString())
			{
				case "Microsoft.Xrm.Sdk.EntityReference":
					objectValue = ((EntityReference) entityValue).Id.ToString();
					objectReference = ((EntityReference) entityValue).LogicalName;
					break;
				case "Microsoft.Xrm.Sdk.OptionSetValue":
					objectValue = ((OptionSetValue) entityValue).Value.ToString();
					break;
				case "Microsoft.Xrm.Sdk.Money":
					objectValue = Convert.ToString(((Money) entityValue).Value, CultureInfo.InvariantCulture.NumberFormat);
					break;
				case "Microsoft.Xrm.Sdk.AliasedValue":
					var av = (AliasedValue) entityValue;
					objectValue = GetAttributeValue(av.Value);
					objectReference = $"{av.EntityLogicalName},{av.AttributeLogicalName}";
					break;
				case "System.Guid":
					objectValue = entityValue.ToString();
					break;
				default:
					if (entityValue.IsNumeric())
					{
						objectValue = Convert.ToString(entityValue, CultureInfo.InvariantCulture.NumberFormat);
					}
					else if (entityValue.IsDateTime())
					{
						objectValue = ((DateTime) entityValue).ToUniversalTime().ToString("u");
					}
					else
					{
						objectValue = entityValue.ToString();
					}
					break;
			}

			if (!string.IsNullOrWhiteSpace(objectReference))
			{
				objectReference += ",";
			}

			return $"{objectType}|{objectReference}{objectValue}";
		}


		public static Entity DeserializeObject(string xml)
		{
			var dataSet = new DataSet();

			var xmlBytes = Encoding.UTF8.GetBytes(xml);

			using (var ms = new MemoryStream(xmlBytes))
			{
				dataSet.ReadXml(ms);
			}
			if (dataSet.Tables.Count <= 0)
			{
				return null;
			}
			var dt = dataSet.Tables[0];
			if (dt.Rows.Count <= 0)
			{
				return null;
			}
			var entity = new Entity(dt.TableName);
			var row = dt.Rows[0];

			foreach (DataColumn column in dt.Columns)
			{
				if (column.ColumnName == "Id")
				{
					entity.Id = (Guid?) SetAttributeValue(row[column.ColumnName]) ?? Guid.Empty;
				}
				else
				{
					entity[column.ColumnName] = SetAttributeValue(row[column.ColumnName]);
				}
			}

			return entity;
		}

		private static object SetAttributeValue(object val)
		{
			if (val == null)
			{
				return null;
			}
			var input = val.ToString();
			var tokens = input.Split('|');

			if (tokens.Length != 2)
			{
				return null;
			}
			switch (tokens[0])
			{
				case "Microsoft.Xrm.Sdk.EntityReference":
					var er = tokens[1].Split(',');
					if (er.Length == 2)
					{
						return new EntityReference(er[0], new Guid(er[1]));
					}
					break;
				case "Microsoft.Xrm.Sdk.OptionSetValue":
					int i;
					int.TryParse(tokens[1], out i);
					if (i != int.MinValue)
					{
						return new OptionSetValue(i);
					}
					break;
				case "Microsoft.Xrm.Sdk.Money":
					decimal d;
					decimal.TryParse(tokens[1], NumberStyles.AllowDecimalPoint, NumberFormatInfo.InvariantInfo, out d);
					if (d != decimal.MinValue)
					{
						return new Money(d);
					}
					break;
				case "Microsoft.Xrm.Sdk.AliasedValue":
					var av = tokens[1].Split(',');
					if (av.Length == 3)
					{
						return new AliasedValue(av[0], av[1], SetAttributeValue(av[2]));
					}
					break;
				case "System.Guid":
					Guid g;
					Guid.TryParse(tokens[1], out g);
					if (g != Guid.Empty)
					{
						return g;
					}
					break;
				case "System.DateTime":
					DateTime dt;
					DateTime.TryParse(tokens[1], out dt);
					if (dt != DateTime.MinValue)
					{
						return dt.ToUniversalTime();
					}
					break;
				case "System.Int32":
					int i32;
					int.TryParse(tokens[1], out i32);
					if (i32 != int.MinValue)
					{
						return i32;
					}
					break;
				case "System.Boolean":
					return (tokens[1].ToUpper() == "TRUE");
				case "System.Decimal":
					decimal sd;
					decimal.TryParse(tokens[1], NumberStyles.AllowDecimalPoint, NumberFormatInfo.InvariantInfo, out sd);
					if (sd != decimal.MinValue)
					{
						return sd;
					}
					break;
				default:
					// all other values
					return tokens[1];
			}
			return null;
		}
	}

	[ExcludeFromCodeCoverage]
	[DebuggerNonUserCode]
	public static class ExtensionMethods
	{
		public static bool IsNumeric(this object obj)
		{
			switch (Type.GetTypeCode(obj.GetType()))
			{
				case TypeCode.Byte:
				case TypeCode.SByte:
				case TypeCode.UInt16:
				case TypeCode.UInt32:
				case TypeCode.UInt64:
				case TypeCode.Int16:
				case TypeCode.Int32:
				case TypeCode.Int64:
				case TypeCode.Decimal:
				case TypeCode.Double:
				case TypeCode.Single:
					return true;
				default:
					return false;
			}
		}

		public static bool IsDateTime(this object obj)
		{
			return Type.GetTypeCode(obj.GetType()) == TypeCode.DateTime;
		}
	}
}
