// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Elastic.Apm.Api;
using Elastic.Apm.BackendComm.CentralConfig;
using Elastic.Apm.Config;
using Elastic.Apm.DiagnosticListeners;
using Elastic.Apm.DiagnosticSource;
using Elastic.Apm.Logging;
using Elastic.Apm.Report;

namespace Elastic.Apm
{
	public interface IApmAgentComponents
	{
		HashSet<Type> SubscribedListeners { get; }
	}

	public interface IApmAgent : IApmAgentComponents
	{
		[Obsolete("Please use Configuration property instead")]
		IConfigurationReader ConfigurationReader { get; }

		IConfigurationReader Configuration { get; }

		IApmLogger Logger { get; }

		IPayloadSender PayloadSender { get; }

		Service Service { get; }

		ITracer Tracer { get; }
	}

	internal class ApmAgent : IApmAgent, IDisposable
	{
		internal readonly CompositeDisposable Disposables = new();
		internal ApmAgent(AgentComponents agentComponents) => Components = agentComponents ?? new AgentComponents();

		internal ICentralConfigurationFetcher CentralConfigurationFetcher => Components.CentralConfigurationFetcher;

		internal AgentComponents Components { get; }
		internal IConfigurationStore ConfigurationStore => Components.ConfigurationStore;
		public IConfigurationReader Configuration => ConfigurationStore.CurrentSnapshot;
		[Obsolete("Please use Configuration property instead")]
		public IConfigurationReader ConfigurationReader => Configuration;

		public IApmLogger Logger => Components.Logger;
		public IPayloadSender PayloadSender => Components.PayloadSender;
		public Service Service => Components.Service;
		public ITracer Tracer => Components.Tracer;
		internal Tracer TracerInternal => Components.TracerInternal;
		internal HttpTraceConfiguration HttpTraceConfiguration => Components.HttpTraceConfiguration;
		HashSet<Type> IApmAgentComponents.SubscribedListeners => ((IApmAgentComponents)Components).SubscribedListeners;

		public void Dispose()
		{
			Disposables?.Dispose();
			Components?.Dispose();
		}
	}

	public static class Agent
	{
		private static readonly Lazy<ApmAgent> LazyApmAgent = new Lazy<ApmAgent>(() =>
		{
			lock (InitializationLock)
			{
				var agent = new ApmAgent(Components);
				agent.Logger?.Trace()
					?.Log("Initialization - Agent instance initialized. Callstack: {callstack}", new StackTrace().ToString());

				if (agent.Components.PayloadSender is not IPayloadSenderWithFilters sender)
					return agent;

				ErrorFilters.ForEach(f => sender.AddFilter(f));
				TransactionFilters.ForEach(f => sender.AddFilter(f));
				SpanFilters.ForEach(f => sender.AddFilter(f));
				agent.Logger?.Trace()
					?.Log(@"Initialization - Added filters to agent (errors:{ErrorFilters}, transactions:{TransactionFilters} spans:{SpanFilters}",
						ErrorFilters.Count, TransactionFilters.Count, SpanFilters.Count);

				return agent;
			}
		});

		private static readonly object InitializationLock = new object();

		private static readonly List<Func<IError, IError>> ErrorFilters = [];

		private static readonly List<Func<ISpan, ISpan>> SpanFilters = [];

		private static readonly List<Func<ITransaction, ITransaction>> TransactionFilters = [];

		internal static AgentComponents Components { get; private set; }

		public static IConfigurationReader Config => Instance.Configuration;

		internal static ApmAgent Instance => LazyApmAgent.Value;

		public static bool IsConfigured => LazyApmAgent.IsValueCreated;

		/// <summary>
		/// The entry point for manual instrumentation. Gets an <see cref="ITracer" /> from
		/// which the currently active transaction and span can be accessed, and enables starting
		/// or capturing a new transaction.
		/// </summary>
		public static ITracer Tracer => Instance.Tracer;

		/// <summary>
		/// Adds a filter which gets called before each transaction gets sent to APM Server.
		/// In the <paramref name="filter" />, you have access to the <see cref="ITransaction" />
		/// instance which gets sent to APM Server and you can modify it. With the return value of the
		/// <paramref name="filter" />, you can also control if the <see cref="ITransaction" />
		/// should be sent to the server or not. If the <paramref name="filter" />
		/// returns a non-null <see cref="ITransaction" /> instance then it will be sent to the APM Server,
		/// and if it returns <code>null</code>, the event will be dropped and won't be sent to the APM server.
		/// </summary>
		/// <param name="filter">
		/// The filter that can process the <see cref="ITransaction" /> and decide if it should be sent to APM
		/// Server or not.
		/// </param>
		/// <returns>
		/// <code>true</code> if the filter was added successfully, <code>false</code> otherwise. In case the method
		/// returns <code>false</code> the filter won't be called.
		/// </returns>
		public static bool AddFilter(Func<ITransaction, ITransaction> filter)
		{
			if (!IsConfigured)
			{
				TransactionFilters.Add(filter);
				return true;
			}

			return CheckAndAddFilter(p => p.AddFilter(filter));
		}

		/// <summary>
		/// Adds a filter which gets called before each span gets sent to APM Server.
		/// In the <paramref name="filter" />, you have access to the <see cref="ISpan" />
		/// instance which gets sent to APM Server and you can modify it. With the return value of the
		/// <paramref name="filter" />, you can also control if the <see cref="ISpan" />
		/// should be sent to the server or not. If the <paramref name="filter" />
		/// returns a non-null <see cref="ISpan" /> instance then it will be sent to the APM Server, and
		/// if it returns <code>null</code>, the event will be dropped and won't be sent to the APM server.
		/// </summary>
		/// <param name="filter">
		/// The filter that can process the <see cref="ISpan" /> and decide if it should be sent to APM Server
		/// or not.
		/// </param>
		/// <returns>
		/// <code>true</code> if the filter was added successfully, <code>false</code> otherwise. In case the method
		/// returns <code>false</code> the filter won't be called.
		/// </returns>
		public static bool AddFilter(Func<ISpan, ISpan> filter)
		{
			if (!IsConfigured)
			{
				SpanFilters.Add(filter);
				return true;
			}

			return CheckAndAddFilter(p => p.AddFilter(filter));
		}

		/// <summary>
		/// Adds a filter which gets called before each error gets sent to APM Server.
		/// In the <paramref name="filter" />, you have access to the <see cref="IError" />
		/// instance which gets sent to APM Server and you can modify it. With the return value of the
		/// <paramref name="filter" /> you can also control if the <see cref="IError" />
		/// should be sent to the server or not. If the <paramref name="filter" />
		/// returns a non-null <see cref="IError" /> instance then it will be sent to the APM Server, and
		/// if it returns <code>null</code>, the event will be dropped and won't be sent to the APM server.
		/// </summary>
		/// <param name="filter">
		/// The filter that can process the <see cref="IError" /> and decide if it should be sent to APM
		/// Server or not.
		/// </param>
		/// <returns>
		/// <code>true</code> if the filter was added successfully, <code>false</code> otherwise. In case the method
		/// returns <code>false</code> the filter won't be called.
		/// </returns>
		public static bool AddFilter(Func<IError, IError> filter)
		{
			if (!IsConfigured)
			{
				ErrorFilters.Add(filter);
				return true;
			}

			return CheckAndAddFilter(p => p.AddFilter(filter));
		}

		private static bool CheckAndAddFilter(Func<IPayloadSenderWithFilters, bool> action)
		{
			if (Instance.PayloadSender is not IPayloadSenderWithFilters sender)
				return false;

			return action(sender);
		}

		/// <summary>
		/// Sets up multiple <see cref="IDiagnosticsSubscriber" />s to start listening to one or more
		/// <see cref="IDiagnosticListener" />s.
		/// <para />
		/// If the agent is not enabled, subscribers are not subscribed.
		/// </summary>
		/// <param name="subscribers">
		/// An array of <see cref="IDiagnosticsSubscriber" /> that will set up <see cref="IDiagnosticListener" /> subscriptions.
		/// </param>
		/// <returns>
		/// A disposable referencing all the subscriptions. Disposing this is not necessary for clean up, only to unsubscribe if
		/// desired.
		/// </returns>
		public static IDisposable Subscribe(params IDiagnosticsSubscriber[] subscribers) => Instance.Subscribe(subscribers);

		public static void Setup(AgentComponents agentComponents)
		{
			lock (InitializationLock)
			{
				if (LazyApmAgent.IsValueCreated)
				{
					Components?.Logger?.Error()
						?.Log("The singleton APM agent has" +
							" already been instantiated and can no longer be configured. Reusing existing instance. "
							+ "Callstack: {callstack}", new StackTrace().ToString());

					// Above line logs on the already configured `Components`
					// In order to let the caller know, we also log on the logger of the rejected `agentComponents`
					agentComponents?.Logger?.Error()
						?.Log("The singleton APM agent has" +
							" already been instantiated and can no longer be configured. Reusing existing instance. "
							+ "Callstack: {callstack}", new StackTrace().ToString());

					return;
				}

				Components ??= agentComponents;

				agentComponents?.Logger?.Trace()
					?.Log("Initialization - Agent.Setup called");


				// Force initialization
				var _ = LazyApmAgent.Value;
			}
		}

		internal static void Setup(ApmAgent apmAgent)
		{
			if (!LazyApmAgent.IsValueCreated)
				Setup(apmAgent.Components);
		}
	}
}
