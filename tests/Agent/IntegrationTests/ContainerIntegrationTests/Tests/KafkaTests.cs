// Copyright 2020 New Relic, Inc. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NewRelic.Agent.ContainerIntegrationTests.Fixtures;
using NewRelic.Agent.IntegrationTestHelpers;
using NewRelic.Testing.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace NewRelic.Agent.ContainerIntegrationTests.Tests;

public abstract class LinuxKafkaTest<T> : NewRelicIntegrationTest<T> where T : KafkaTestFixtureBase
{
    private const int TopicNameLength = 15;

    private readonly string _topicName;
    private readonly T _fixture;

    protected LinuxKafkaTest(T fixture, ITestOutputHelper output) : base(fixture)
    {
        _fixture = fixture;
        _fixture.TestLogger = output;

        _topicName = GenerateTopic();

        _fixture.Actions(setupConfiguration: () =>
            {
                var configModifier = new NewRelicConfigModifier(_fixture.DestinationNewRelicConfigFilePath);
                configModifier.SetLogLevel("debug");
                configModifier.ConfigureFasterMetricsHarvestCycle(10);
                configModifier.LogToConsole();

                _fixture.RemoteApplication.SetAdditionalEnvironmentVariable("NEW_RELIC_KAFKA_TOPIC", _topicName);
            },
            exerciseApplication: () =>
            {
                _fixture.Delay(15); // wait long enough to ensure kafka and app are ready
                _fixture.ExerciseApplication();

                _fixture.Delay(11); // wait long enough to ensure a metric harvest occurs after we exercise the app
                _fixture.AgentLog.WaitForLogLine(AgentLogBase.HarvestFinishedLogLineRegex, TimeSpan.FromSeconds(11));

                // shut down the container and wait for the agent log to see it
                _fixture.ShutdownRemoteApplication();
                _fixture.AgentLog.WaitForLogLine(AgentLogBase.ShutdownLogLineRegex, TimeSpan.FromSeconds(10));
            });

        _fixture.Initialize();
    }

    [Fact]
    public void Test()
    {
        var messageBrokerProduce = "MessageBroker/Kafka/Topic/Produce/Named/" + _topicName;
        var messageBrokerProduceSerializationKey = messageBrokerProduce + "/Serialization/Key";
        var messageBrokerProduceSerializationValue = messageBrokerProduce + "/Serialization/Value";

        var messageBrokerConsume = "MessageBroker/Kafka/Topic/Consume/Named/" + _topicName;

        var consumeTransactionName = @"OtherTransaction/Message/Kafka/Topic/Consume/Named/" + _topicName;
        var produceWebTransactionName = @"WebTransaction/MVC/Kafka/Produce";

        var metrics = _fixture.AgentLog.GetMetrics();
        var spans = _fixture.AgentLog.GetSpanEvents();
        var produceSpan = spans.FirstOrDefault(s => s.IntrinsicAttributes["name"].Equals(messageBrokerProduce));
        var consumeTxnSpan = spans.FirstOrDefault(s => s.IntrinsicAttributes["name"].Equals(consumeTransactionName));

        var expectedMetrics = new List<Assertions.ExpectedMetric>
        {
            new Assertions.ExpectedMetric { metricName = produceWebTransactionName, callCount = 2 }, // includes sync and async actions
            new Assertions.ExpectedMetric { metricName = messageBrokerProduce, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerProduce, metricScope = produceWebTransactionName, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerProduceSerializationKey, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerProduceSerializationKey, metricScope = produceWebTransactionName, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerProduceSerializationValue, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerProduceSerializationValue, metricScope = produceWebTransactionName, callCount = 2 },

            new Assertions.ExpectedMetric { metricName = consumeTransactionName, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerConsume, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = messageBrokerConsume, metricScope = consumeTransactionName, callCount = 2 },
            new Assertions.ExpectedMetric { metricName = "Supportability/TraceContext/Create/Success", callCount = 2 },
            new Assertions.ExpectedMetric { metricName = "Supportability/TraceContext/Accept/Success", callCount = 2 },
        };

        NrAssert.Multiple(
            () => Assertions.MetricsExist(expectedMetrics, metrics),
            () => Assert.True(produceSpan.IntrinsicAttributes.ContainsKey("traceId")),
            () => Assert.True(produceSpan.IntrinsicAttributes.ContainsKey("parentId")),
            () => Assert.NotNull(consumeTxnSpan),
            () => Assert.True(consumeTxnSpan.UserAttributes.ContainsKey("kafka.consume.byteCount")),
            () => Assert.InRange((long)consumeTxnSpan.UserAttributes["kafka.consume.byteCount"], 450, 470), // includes headers
            () => Assert.True(consumeTxnSpan.IntrinsicAttributes.ContainsKey("traceId")),
            () => Assert.True(consumeTxnSpan.IntrinsicAttributes.ContainsKey("parentId"))
        );
    }

    internal static string GenerateTopic()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < TopicNameLength; i++)
        {
            var shifter= RandomNumberGenerator.GetInt32(0, 26);
            builder.Append(Convert.ToChar(shifter + 65));
        }

        return builder.ToString();
    }
}

public class KafkaDotNet6Test : LinuxKafkaTest<KafkaDotNet6TestFixture>
{
    public KafkaDotNet6Test(KafkaDotNet6TestFixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }
}

public class KafkaDotNet8Test : LinuxKafkaTest<KafkaDotNet8TestFixture>
{
    public KafkaDotNet8Test(KafkaDotNet8TestFixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }
}
