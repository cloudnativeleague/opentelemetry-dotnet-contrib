// <copyright file="TelemetryBindingElementForHttpTests.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading;
using System.Threading.Tasks;
using OpenTelemetry.Instrumentation.Wcf.Tests.Tools;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

[Collection("WCF")]
public class TelemetryBindingElementForHttpTests : IDisposable
{
    private readonly Uri serviceBaseUri;
    private readonly HttpListener listener;
    private readonly EventWaitHandle initializationHandle;

    public TelemetryBindingElementForHttpTests()
    {
        Random random = new Random();
        var retryCount = 5;
        while (retryCount > 0)
        {
            try
            {
                this.serviceBaseUri = new Uri($"http://localhost:{random.Next(2000, 5000)}/");

                this.listener = new HttpListener();
                this.listener.Prefixes.Add(this.serviceBaseUri.OriginalString);
                this.listener.Start();
                break;
            }
            catch
            {
                this.listener.Close();
                this.listener = null;
                retryCount--;
            }
        }

        if (this.listener == null)
        {
            throw new InvalidOperationException("HttpListener could not be started.");
        }

        this.initializationHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        try
        {
            Listener();

            this.initializationHandle.WaitOne();
        }
        finally
        {
            this.initializationHandle.Dispose();
            this.initializationHandle = null;
        }

        async void Listener()
        {
            while (true)
            {
                try
                {
                    var ctxTask = this.listener.GetContextAsync();

                    this.initializationHandle?.Set();

                    var ctx = await ctxTask.ConfigureAwait(false);

                    using StreamReader reader = new StreamReader(ctx.Request.InputStream);

                    string request = reader.ReadToEnd();

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/xml; charset=utf-8";

                    using (StreamWriter writer = new StreamWriter(ctx.Response.OutputStream))
                    {
                        if (request.Contains("ExecuteWithEmptyActionName"))
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteWithEmptyActionNameResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteWithEmptyActionNameResponse></s:Body></s:Envelope>");
                        }
                        else if (request.Contains("ExecuteSynchronous"))
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteSynchronousResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteSynchronousResponse></s:Body></s:Envelope>");
                        }
                        else
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteResponse></s:Body></s:Envelope>");
                        }
                    }

                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    if (ex is ObjectDisposedException
                        || (ex is HttpListenerException httpEx && httpEx.ErrorCode == 995))
                    {
                        // Listener was closed before we got into GetContextAsync or
                        // Listener was closed while we were in GetContextAsync.
                        break;
                    }

                    throw;
                }
            }
        }
    }

    public void Dispose()
    {
        if (this.listener != null)
        {
            (this.listener as IDisposable).Dispose();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(true, false, false)]
    [InlineData(false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, false, true, true, true, true, true)]
    public async Task OutgoingRequestInstrumentationTest(
        bool instrument,
        bool filter = false,
        bool suppressDownstreamInstrumentation = true,
        bool includeVersion = false,
        bool enrich = false,
        bool enrichmentException = false,
        bool emptyOrNullAction = false)
    {
        List<Activity> stoppedActivities = new List<Activity>();

        var builder = Sdk.CreateTracerProviderBuilder()
            .AddInMemoryExporter(stoppedActivities);

        if (instrument)
        {
            builder
                .AddWcfInstrumentation(options =>
                {
                    if (enrich)
                    {
                        if (!enrichmentException)
                        {
                            options.Enrich = (activity, eventName, message) =>
                            {
                                switch (eventName)
                                {
                                    case WcfEnrichEventNames.BeforeSendRequest:
                                        activity.SetTag("client.beforesendrequest", WcfEnrichEventNames.BeforeSendRequest);
                                        break;
                                    case WcfEnrichEventNames.AfterReceiveReply:
                                        activity.SetTag("client.afterreceivereply", WcfEnrichEventNames.AfterReceiveReply);
                                        break;
                                }
                            };
                        }
                        else
                        {
                            options.Enrich = (activity, eventName, message) => throw new Exception("Error while enriching activity");
                        }
                    }

                    options.OutgoingRequestFilter = (Message m) => !filter;
                    options.SuppressDownstreamInstrumentation = suppressDownstreamInstrumentation;
                    options.SetSoapMessageVersion = includeVersion;
                })
                .AddDownstreamInstrumentation();
        }

        TracerProvider tracerProvider = builder.Build();

        ServiceClient client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new DownstreamInstrumentationEndpointBehavior());
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            if (emptyOrNullAction)
            {
                await client.ExecuteWithEmptyActionNameAsync(
                    new ServiceRequest
                    {
                        Payload = "Hello Open Telemetry!",
                    }).ConfigureAwait(false);
            }
            else
            {
                await client.ExecuteAsync(
                    new ServiceRequest
                    {
                        Payload = "Hello Open Telemetry!",
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        if (instrument)
        {
            if (!suppressDownstreamInstrumentation)
            {
                Assert.NotEmpty(stoppedActivities);
                if (filter)
                {
                    Assert.Single(stoppedActivities);
                    Assert.Equal("DownstreamInstrumentation", stoppedActivities[0].OperationName);
                }
                else
                {
                    Assert.Equal(2, stoppedActivities.Count);
                    Assert.NotNull(stoppedActivities.SingleOrDefault(activity => activity.OperationName == "DownstreamInstrumentation"));
                    Assert.NotNull(stoppedActivities.SingleOrDefault(activity => activity.OperationName == WcfInstrumentationActivitySource.OutgoingRequestActivityName));
                }
            }
            else
            {
                if (!filter)
                {
                    Assert.NotEmpty(stoppedActivities);
                    Assert.Single(stoppedActivities);

                    Activity activity = stoppedActivities[0];

                    if (emptyOrNullAction)
                    {
                        Assert.Equal(WcfInstrumentationActivitySource.OutgoingRequestActivityName, activity.DisplayName);
                        Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.RpcMethodTag).Value);
                    }
                    else
                    {
                        Assert.Equal("http://opentelemetry.io/Service/Execute", activity.DisplayName);
                        Assert.Equal("Execute", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.RpcMethodTag).Value);
                    }

                    Assert.Equal(WcfInstrumentationActivitySource.OutgoingRequestActivityName, activity.OperationName);
                    Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.RpcSystemTag).Value);
                    Assert.Equal("http://opentelemetry.io/Service", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.RpcServiceTag).Value);
                    Assert.Equal(this.serviceBaseUri.Host, activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.NetPeerNameTag).Value);
                    Assert.Equal(this.serviceBaseUri.Port, activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.NetPeerPortTag).Value);
                    Assert.Equal("http", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.WcfChannelSchemeTag).Value);
                    Assert.Equal("/Service", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.WcfChannelPathTag).Value);
                    if (includeVersion)
                    {
                        Assert.Equal("Soap11 (http://schemas.xmlsoap.org/soap/envelope/) AddressingNone (http://schemas.microsoft.com/ws/2005/05/addressing/none)", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.SoapMessageVersionTag).Value);
                    }

                    if (enrich && !enrichmentException)
                    {
                        Assert.Equal(WcfEnrichEventNames.BeforeSendRequest, activity.TagObjects.Single(t => t.Key == "client.beforesendrequest").Value);
                        Assert.Equal(WcfEnrichEventNames.AfterReceiveReply, activity.TagObjects.Single(t => t.Key == "client.afterreceivereply").Value);
                    }
                }
                else
                {
                    Assert.Empty(stoppedActivities);
                }
            }
        }
        else
        {
            Assert.Empty(stoppedActivities);
        }
    }

    [Fact]
    public async void ActivitiesHaveCorrectParentTest()
    {
        var testSource = new ActivitySource("TestSource");

        var stoppedActivities = new List<Activity>();
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("TestSource")
            .AddInMemoryExporter(stoppedActivities)
            .AddWcfInstrumentation()
            .Build();

        ServiceClient client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            using (var parentActivity = testSource.StartActivity("ParentActivity"))
            {
                client.ExecuteSynchronous(new ServiceRequest { Payload = "Hello Open Telemetry!" });
                client.ExecuteSynchronous(new ServiceRequest { Payload = "Hello Open Telemetry!" });
                var firstAsyncCall = client.ExecuteAsync(new ServiceRequest { Payload = "Hello Open Telemetry!" });
                await client.ExecuteAsync(new ServiceRequest { Payload = "Hello Open Telemetry!" });
                await firstAsyncCall;
            }
        }
        finally
        {
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            testSource.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.Equal(5, stoppedActivities.Count);
        Assert.All(stoppedActivities, activity => Assert.Equal(stoppedActivities[0].TraceId, activity.TraceId));
        var parent = stoppedActivities.Single(activity => activity.Parent == null);
        Assert.All(
            stoppedActivities.Where(activity => activity != parent),
            activity => Assert.Equal(parent.SpanId, activity.ParentSpanId));
    }

    [Fact]
    public async void ErrorsAreHandledProperlyTest()
    {
        var testSource = new ActivitySource("TestSource");

        var stoppedActivities = new List<Activity>();
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("TestSource")
            .AddInMemoryExporter(stoppedActivities)
            .AddWcfInstrumentation()
            .Build();

        ServiceClient client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        ServiceClient clientBadUrl = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri("http://localhost:1/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());
            clientBadUrl.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            using (var parentActivity = testSource.StartActivity("ParentActivity"))
            {
                Assert.ThrowsAny<Exception>(() => client.ErrorSynchronous());
                await Assert.ThrowsAnyAsync<Exception>(client.ErrorAsync);
                Assert.ThrowsAny<Exception>(() => clientBadUrl.ExecuteSynchronous(new ServiceRequest { Payload = "Hello Open Telemetry!" }));
                await Assert.ThrowsAnyAsync<Exception>(() => clientBadUrl.ExecuteAsync(new ServiceRequest { Payload = "Hello Open Telemetry!" }));
            }
        }
        finally
        {
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            if (clientBadUrl.State == CommunicationState.Faulted)
            {
                clientBadUrl.Abort();
            }
            else
            {
                clientBadUrl.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            testSource.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.Equal(5, stoppedActivities.Count);
        Assert.All(stoppedActivities, activity => Assert.Equal(stoppedActivities[0].TraceId, activity.TraceId));
        var parent = stoppedActivities.Single(activity => activity.Parent == null);
        var children = stoppedActivities.Where(activity => activity != parent);
        Assert.All(children, activity => Assert.Equal(parent.SpanId, activity.ParentSpanId));
    }
}
