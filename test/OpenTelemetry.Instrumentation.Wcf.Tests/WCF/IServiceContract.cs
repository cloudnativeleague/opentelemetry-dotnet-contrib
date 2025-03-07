// <copyright file="IServiceContract.cs" company="OpenTelemetry Authors">
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

using System.ServiceModel;
using System.Threading.Tasks;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

[ServiceContract(Namespace = "http://opentelemetry.io/", Name = "Service", SessionMode = SessionMode.Allowed)]
public interface IServiceContract
{
    [OperationContract]
    Task<ServiceResponse> ExecuteAsync(ServiceRequest request);

    [OperationContract]
    ServiceResponse ExecuteSynchronous(ServiceRequest request);

    [OperationContract(Action = "")]
    Task<ServiceResponse> ExecuteWithEmptyActionNameAsync(ServiceRequest request);

    [OperationContract(IsOneWay = true)]
    void ExecuteWithOneWay(ServiceRequest request);

    [OperationContract]
    void ErrorSynchronous();

    [OperationContract]
    Task ErrorAsync();
}
