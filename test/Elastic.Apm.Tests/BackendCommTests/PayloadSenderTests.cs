// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.BackendComm;
using Elastic.Apm.Config;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;
using Elastic.Apm.Report;
using Elastic.Apm.ServerInfo;
using Elastic.Apm.Tests.Utilities;
using FluentAssertions;
using FluentAssertions.Extensions;
using RichardSzalay.MockHttp;
using Xunit;
using Xunit.Abstractions;
using MockHttpMessageHandler = Elastic.Apm.Tests.Utilities.MockHttpMessageHandler;

namespace Elastic.Apm.Tests.BackendCommTests
{
	public class PayloadSenderTests : LoggingTestBase
	{
		private const string ThisClassName = nameof(PayloadSenderTests);

		private static readonly IEnumerable<TimeSpan?> FlushIntervalVariants = new TimeSpan?[]
		{
			null, ConfigConsts.DefaultValues.FlushIntervalInMilliseconds.Milliseconds(), TimeSpan.Zero, 10.Milliseconds(), 100.Milliseconds(),
			1.Seconds(), 1.Hours(), 1.Days()
		};

		private static readonly TimeSpan VeryLongFlushInterval = 1.Hours();
		private static readonly TimeSpan VeryShortFlushInterval = 1.Seconds();
		private readonly IApmLogger _logger;

		public PayloadSenderTests(ITestOutputHelper xUnitOutputHelper) : base(xUnitOutputHelper /*, LogLevel.Debug */) =>
			_logger = LoggerBase.Scoped(ThisClassName);

		public static IEnumerable<object[]> TestArgsVariantsWithVeryLongFlushInterval =>
			TestArgsVariants(args => args.FlushInterval.HasValue && args.FlushInterval >= VeryLongFlushInterval).Select(t => new object[] { t });


		[Fact]
		public void Should_Sanitize_HttpRequestMessage_In_Log()
		{
			var testLogger = new TestLogger(LogLevel.Trace);
			var secretToken = "secretToken";
			var serverUrl = "http://username:password@localhost:8200";

			var config = new MockConfiguration(testLogger, logLevel: "Trace", serverUrl: serverUrl, secretToken: secretToken, flushInterval: "0");
			var service = Service.GetDefaultService(config, testLogger);
			var waitHandle = new ManualResetEvent(false);
			var handler = new RichardSzalay.MockHttp.MockHttpMessageHandler();
			var configUrl = BackendCommUtils.ApmServerEndpoints
				.BuildIntakeV2EventsAbsoluteUrl(config.ServerUrl);

			handler.When(configUrl.AbsoluteUri)
				.Respond(_ =>
				{
					waitHandle.Set();
					return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
				});

			var payloadSender = new PayloadSenderV2(testLogger, config, service, new Api.System(), MockApmServerInfo.Version710, handler);
			using var agent = new ApmAgent(new TestAgentComponents(LoggerBase, config, payloadSender));
			agent.PayloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));

			waitHandle.WaitOne();

			var count = 0;
			while (!testLogger.Log.Contains("Failed sending event.")
				   && count < 10)
			{
				Thread.Sleep(500);
				count++;
			}

			testLogger.Log.Should()
				.NotContain(secretToken)
				.And.Contain("http://[REDACTED]:[REDACTED]@localhost:8200")
				.And.NotContain(serverUrl);
		}

		[Fact]
		public async Task SecretToken_ShouldBeSent_WhenApiKeyIsNotSpecified()
		{
			// Arrange
			const string secretToken = "SecretToken";

			var isRequestFinished = new TaskCompletionSource<object>();

			AuthenticationHeaderValue authHeader = null;
			var handler = new MockHttpMessageHandler((r, _) =>
			{
				authHeader = r.Headers.Authorization;
				isRequestFinished.SetResult(null);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			var noopLogger = new NoopLogger();
			var mockConfig = new MockConfiguration(_logger, secretToken: secretToken, maxBatchEventCount: "1");
			var payloadSender = new PayloadSenderV2(_logger, mockConfig,
				Service.GetDefaultService(mockConfig, noopLogger), new Api.System(), MockApmServerInfo.Version710, handler, /* dbgName: */
				TestDisplayName);

			// Act
			using (var agent = new ApmAgent(new TestAgentComponents(LoggerBase, mockConfig, payloadSender)))
			{
				agent.PayloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));
				await isRequestFinished.Task;
			}

			// Assert
			authHeader.Should().NotBeNull();
			authHeader.Scheme.Should().Be("Bearer");
			authHeader.Parameter.Should().Be(secretToken);
		}

		[Fact]
		public async Task ApiKey_ShouldBeSent_WhenApiKeyAndSecretTokenAreSpecified()
		{
			// Arrange
			const string secretToken = "SecretToken";
			const string apiKey = "ApiKey";

			var isRequestFinished = new TaskCompletionSource<object>();

			AuthenticationHeaderValue authHeader = null;
			var handler = new MockHttpMessageHandler((r, _) =>
			{
				authHeader = r.Headers.Authorization;
				isRequestFinished.SetResult(null);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			var noopLogger = new NoopLogger();
			var mockConfig = new MockConfiguration(_logger, secretToken: secretToken, apiKey: apiKey, maxBatchEventCount: "1");
			var payloadSender = new PayloadSenderV2(_logger, mockConfig,
				Service.GetDefaultService(mockConfig, noopLogger), new Api.System(), MockApmServerInfo.Version710, handler, /* dbgName: */
				TestDisplayName);

			// Act
			using (var agent = new ApmAgent(new TestAgentComponents(LoggerBase, mockConfig, payloadSender)))
			{
				agent.PayloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));
				await isRequestFinished.Task;
			}

			// Assert
			authHeader.Should().NotBeNull();
			authHeader.Scheme.Should().Be("ApiKey");
			authHeader.Parameter.Should().Be(apiKey);
		}

		[Fact]
		public async Task UserAgent_test()
		{
			var isRequestFinished = new TaskCompletionSource<object>();

			HttpHeaderValueCollection<ProductInfoHeaderValue> userAgentHeader = null;
			var handler = new MockHttpMessageHandler((r, _) =>
			{
				userAgentHeader = r.Headers.UserAgent;
				isRequestFinished.SetResult(null);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			var logger = new NoopLogger();
			var service = Service.GetDefaultService(new MockConfiguration(logger), logger);
			var payloadSender = new PayloadSenderV2(logger, new MockConfiguration(logger, flushInterval: "1s"),
				service, new Api.System(), MockApmServerInfo.Version710, handler, /* dbgName: */ TestDisplayName);

			using (var agent = new ApmAgent(new TestAgentComponents(LoggerBase, payloadSender: payloadSender)))
			{
				agent.PayloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));
				await isRequestFinished.Task;
			}

			var headerValues = userAgentHeader.ToList();

			headerValues
				.Should()
				.NotBeEmpty()
				.And.HaveCount(4);

			headerValues[0].Product.Name.Should().Be($"apm-agent-{Consts.AgentName}");
			headerValues[0].Product.Version.Should().NotBeEmpty();

			// (<service name> <service version>)
			headerValues[1].Comment.Should().StartWith("(").And.EndWith(")");

			headerValues[2].Product.Name.Should().Be("System.Net.Http");
			headerValues[2].Product.Version.Should().NotBeEmpty();

			headerValues[3].Product.Name.Should().NotBeEmpty();
			headerValues[3].Product.Version.Should().NotBeEmpty();
		}

		private static IEnumerable<TestArgs> TestArgsVariantsWithoutIndex()
		{
			yield return new TestArgs();

			var maxQueueEventCountVariants = new int?[] { null, 1, 2, 3, 10, ConfigConsts.DefaultValues.MaxQueueEventCount };
			var batchVsQueueCountDeltas = new[] { -2, -1, 0 };

			foreach (var flushInterval in FlushIntervalVariants)
			{
				foreach (var maxQueueEventCount in maxQueueEventCountVariants)
				{
					if (maxQueueEventCount == null)
						continue;

					foreach (var delta in batchVsQueueCountDeltas)
					{
						var maxBatchEventCount = maxQueueEventCount + delta;
						if (maxBatchEventCount < 1)
							continue;

						yield return new TestArgs
						{
							FlushInterval = flushInterval,
							MaxBatchEventCount = maxBatchEventCount,
							MaxQueueEventCount = maxQueueEventCount
						};
					}
				}
			}
		}

		private static IEnumerable<TestArgs> TestArgsVariants(Func<TestArgs, bool> predicate = null)
		{
			var counter = 0;
			foreach (var argsVariant in TestArgsVariantsWithoutIndex())
			{
				if (predicate != null && !predicate(argsVariant))
					continue;

				argsVariant.ArgsIndex = counter++;
				yield return argsVariant;
			}
		}

		private static async Task<bool> EnqueueDummyEvent(PayloadSenderV2 payloadSender, ApmAgent agent, int txIndex) =>
			await payloadSender.EnqueueEventInternal(new Transaction(agent, $"Tx #{txIndex}", "TestType"), "Transaction");

		public static IEnumerable<object[]> MaxBatchEventCount_test_variants()
		{
			var numberOfBatchesVariants = new[] { 1, 2, 3, 10 };
			foreach (var args in TestArgsVariantsWithVeryLongFlushInterval)
			{
				foreach (var numberOfBatches in numberOfBatchesVariants)
					yield return new[] { args[0], numberOfBatches };
			}
		}

		[Theory]
		[MemberData(nameof(MaxBatchEventCount_test_variants))]
		internal async Task MaxBatchEventCount_test(TestArgs args, int expectedNumberOfBatches)
		{
			var expectedNumberOfBatchesSentTcs = new TaskCompletionSource<object>();

			var actualNumberOfBatches = 0;
			var handler = new MockHttpMessageHandler((_, _) =>
			{
				if (Interlocked.Increment(ref actualNumberOfBatches) == expectedNumberOfBatches)
					expectedNumberOfBatchesSentTcs.SetResult(null);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			var configurationReader = args.BuildConfig(_logger);
			var service = Service.GetDefaultService(configurationReader, _logger);
			var payloadSender = new PayloadSenderV2(_logger, configurationReader, service, new Api.System(), MockApmServerInfo.Version710, handler
				, /* dbgName: */ TestDisplayName);

			using (var agent = new ApmAgent(new TestAgentComponents(_logger, payloadSender: payloadSender)))
			{
				var numberOfEventsEnqueuedSuccessfully = 0;
				for (var txIndex = 1; ; ++txIndex)
				{
					if (await EnqueueDummyEvent(payloadSender, agent, txIndex))
						++numberOfEventsEnqueuedSuccessfully;

					if (numberOfEventsEnqueuedSuccessfully == expectedNumberOfBatches * args.MaxBatchEventCount)
						break;
				}

				(await Task.WhenAny(expectedNumberOfBatchesSentTcs.Task, Task.Delay(30.Seconds())))
					.Should()
					.Be(expectedNumberOfBatchesSentTcs.Task
						, $"because numberOfEventsEnqueuedSuccessfully: {numberOfEventsEnqueuedSuccessfully}," +
						$"actualNumberOfBatches: {actualNumberOfBatches} ");
			}
		}

		public static IEnumerable<object[]> FlushInterval_test_variants()
		{
			var argsVariantsCounter = 0;
			var numberOfEventsToSendVariants = new[] { 1, 2, 3, 10 };

			foreach (var flushInterval in FlushIntervalVariants.Where(x => x.HasValue && x.Value <= VeryShortFlushInterval))
			{
				foreach (var numberOfEventsToSend in numberOfEventsToSendVariants)
				{
					yield return new object[]
					{
						new TestArgs { ArgsIndex = argsVariantsCounter++, FlushInterval = flushInterval }, numberOfEventsToSend
					};
				}
			}
		}

		[Theory]
		[MemberData(nameof(FlushInterval_test_variants))]
		internal async Task FlushInterval_test(TestArgs args, int numberOfEventsToSend)
		{
			var batchSentBarrier = new Barrier(2);
			var barrierTimeout = 30.Seconds();

			var handler = new MockHttpMessageHandler((_, _) =>
			{
				batchSentBarrier.SignalAndWait(barrierTimeout).Should().BeTrue();
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			var configurationReader = args.BuildConfig(_logger);
			var service = Service.GetDefaultService(configurationReader, _logger);
			var payloadSender = new PayloadSenderV2(_logger, configurationReader, service, new Api.System(), MockApmServerInfo.Version710,
				handler, /* dbgName: */ TestDisplayName);

			using (var agent = new ApmAgent(new TestAgentComponents(_logger, payloadSender: payloadSender)))
			{
				for (var eventIndex = 1; eventIndex <= numberOfEventsToSend; ++eventIndex)
				{
					(await EnqueueDummyEvent(payloadSender, agent, eventIndex)).Should().BeTrue($"eventIndex: {eventIndex}, args: {args}");
					batchSentBarrier.SignalAndWait(barrierTimeout).Should().BeTrue($"eventIndex: {eventIndex}, args: {args}");
				}
			}
		}

		[Fact]
		public void Dispose_stops_the_thread()
		{
			PayloadSenderV2 lastPayloadSender = null;
			CreateSutEnvAndTest((_, payloadSender) =>
			{
				lastPayloadSender = payloadSender;
				lastPayloadSender.IsRunning.Should().BeTrue();
			});
			lastPayloadSender.IsRunning.Should().BeFalse();

			CreateSutEnvAndTest((agent, payloadSender) =>
			{
				lastPayloadSender = payloadSender;
				lastPayloadSender.IsRunning.Should().BeTrue();

				payloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));
			});
			lastPayloadSender.IsRunning.Should().BeFalse();
		}

		/// <summary>
		/// Makes sure <see cref="BackendCommComponentBase.Dispose"/> finishes without exception and doesn't cause deadlock.
		/// </summary>
		[Fact]
		public void PayloadSenderV2DisposeTest()
		{
			using (var payloadSenderV2 = new PayloadSenderV2(new NoopLogger(), new MockConfiguration(),
					   Service.GetDefaultService(new MockConfiguration(), new NoopLogger()), new Api.System(),
					   new ApmServerInfo()))
				Thread.Sleep(1000);
		}

		private void CreateSutEnvAndTest(Action<ApmAgent, PayloadSenderV2> doAction)
		{
			var configReader = new MockConfiguration(_logger);
			var mockHttpMessageHandler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
			var service = Service.GetDefaultService(configReader, _logger);
			var payloadSender = new PayloadSenderV2(_logger, configReader, service, new Api.System(), MockApmServerInfo.Version710,
				mockHttpMessageHandler
				, /* dbgName: */ TestDisplayName);

			payloadSender.IsRunning.Should().BeTrue();

			using (var agent = new ApmAgent(new TestAgentComponents(LoggerBase, payloadSender: payloadSender)))
				doAction(agent, payloadSender);

			payloadSender.IsRunning.Should().BeFalse();
		}

		internal class TestArgs
		{
			internal int ArgsIndex { get; set; }
			internal TimeSpan? FlushInterval { get; set; }
			internal int? MaxBatchEventCount { get; set; }
			internal int? MaxQueueEventCount { get; set; }

			internal MockConfiguration BuildConfig(IApmLogger logger) =>
				new MockConfiguration(logger
					, flushInterval: FlushInterval.HasValue ? $"{FlushInterval.Value.TotalMilliseconds}ms" : null
					, maxBatchEventCount: MaxBatchEventCount?.ToString()
					, maxQueueEventCount: MaxQueueEventCount?.ToString());

			public override string ToString() => new ToStringBuilder("")
			{
				{ nameof(ArgsIndex), ArgsIndex },
				{ nameof(MaxQueueEventCount), MaxQueueEventCount },
				{ nameof(MaxBatchEventCount), MaxBatchEventCount },
				{ nameof(FlushInterval), (FlushInterval?.ToHms()).AsNullableToString() }
			}.ToString();
		}
	}
}
