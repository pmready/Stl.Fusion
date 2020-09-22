using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using RestEase;
using Stl.DependencyInjection;
using Stl.Fusion.Bridge;
using Stl.Fusion.Bridge.Interception;
using Stl.Fusion.Client.Authentication;
using Stl.Fusion.Client.RestEase;
using Stl.Fusion.Client.RestEase.Internal;
using Stl.Fusion.Internal;
using Stl.Reflection;
using Stl.Serialization;

namespace Stl.Fusion.Client
{
    public readonly struct FusionRestEaseClientBuilder
    {
        private class AddedTag { }
        private static readonly ServiceDescriptor AddedTagDescriptor =
            new ServiceDescriptor(typeof(AddedTag), new AddedTag());

        public FusionBuilder Fusion { get; }
        public IServiceCollection Services => Fusion.Services;

        internal FusionRestEaseClientBuilder(FusionBuilder fusion)
        {
            Fusion = fusion;
            if (Services.Contains(AddedTagDescriptor))
                return;
            // We want above Contains call to run in O(1), so...
            Services.Insert(0, AddedTagDescriptor);

            Fusion.AddReplicator();
            Services.TryAddSingleton<WebSocketChannelProvider.Options>();
            Services.TryAddSingleton<IChannelProvider, WebSocketChannelProvider>();

            // FusionHttpMessageHandler (handles Fusion headers)
            Services.AddHttpClient();
            Services.TryAddEnumerable(ServiceDescriptor.Singleton<
                IHttpMessageHandlerBuilderFilter,
                FusionHttpMessageHandlerBuilderFilter>());
            Services.TryAddTransient<FusionHttpMessageHandler>();

            // InterfaceCastProxyGenerator (used by ReplicaServices)
            Services.TryAddSingleton<InterfaceCastInterceptor>();
            Services.TryAddSingleton(c => InterfaceCastProxyGenerator.Default);
            Services.TryAddSingleton(c => new [] { c.GetRequiredService<InterfaceCastInterceptor>() });

            // ResponseDeserializer & ReplicaResponseDeserializer
            Services.TryAddTransient<ResponseDeserializer>(c => new JsonResponseDeserializer() {
                JsonSerializerSettings = JsonNetSerializer.DefaultSettings
            });
            Services.TryAddTransient<RequestBodySerializer>(c => new JsonRequestBodySerializer() {
                JsonSerializerSettings = JsonNetSerializer.DefaultSettings
            });
        }

        public FusionBuilder BackToFusion() => Fusion;
        public IServiceCollection BackToServices() => Services;

        // ConfigureXxx

        public FusionRestEaseClientBuilder ConfigureHttpClientFactory(
            Action<IServiceProvider, string?, HttpClientFactoryOptions> httpClientFactoryOptionsBuilder)
        {
            Services.Configure(httpClientFactoryOptionsBuilder);
            return this;
        }

        public FusionRestEaseClientBuilder ConfigureWebSocketChannel(
            WebSocketChannelProvider.Options options)
        {
            var serviceDescriptor = new ServiceDescriptor(
                typeof(WebSocketChannelProvider.Options),
                options);
            Services.Replace(serviceDescriptor);
            return this;
        }

        public FusionRestEaseClientBuilder ConfigureWebSocketChannel(
            Action<IServiceProvider, WebSocketChannelProvider.Options> optionsBuilder)
        {
            var serviceDescriptor = new ServiceDescriptor(
                typeof(WebSocketChannelProvider.Options),
                c => {
                    var options = new WebSocketChannelProvider.Options();
                    optionsBuilder.Invoke(c, options);
                    return options;
                },
                ServiceLifetime.Singleton);
            Services.Replace(serviceDescriptor);
            return this;
        }

        // User-defined client-side services

        public FusionRestEaseClientBuilder AddClientService<TClient>(string? clientName = null)
            => AddClientService(typeof(TClient), clientName);
        public FusionRestEaseClientBuilder AddClientService(Type clientType, string? clientName = null)
        {
            if (!(clientType.IsInterface && clientType.IsVisible))
                throw Internal.Errors.InterfaceTypeExpected(clientType, true, nameof(clientType));
            clientName ??= clientType.FullName;

            Services.TryAddSingleton(clientType, c => {
                var httpClientFactory = c.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient(clientName);
                var restClient = new RestClient(httpClient) {
                    RequestBodySerializer = c.GetRequiredService<RequestBodySerializer>(),
                    ResponseDeserializer = c.GetRequiredService<ResponseDeserializer>(),
                };
                return restClient.For(clientType);
            });
            return this;
        }

        public FusionRestEaseClientBuilder AddReplicaService<TClient>(
            string? clientName = null)
            where TClient : IRestEaseReplicaClient
            => AddReplicaService(typeof(TClient), clientName);
        public FusionRestEaseClientBuilder AddReplicaService<TService, TClient>(
            string? clientName = null)
            where TClient : IRestEaseReplicaClient
            => AddReplicaService(typeof(TService), typeof(TClient), clientName);
        public FusionRestEaseClientBuilder AddReplicaService(
            Type clientType,
            string? clientName = null)
            => AddReplicaService(clientType, clientType, clientName);
        public FusionRestEaseClientBuilder AddReplicaService(
            Type serviceType,
            Type clientType,
            string? clientName = null)
        {
            if (!(serviceType.IsInterface && serviceType.IsVisible))
                throw Internal.Errors.InterfaceTypeExpected(serviceType, true, nameof(serviceType));
            if (!(clientType.IsInterface && clientType.IsVisible))
                throw Internal.Errors.InterfaceTypeExpected(clientType, true, nameof(clientType));
            if (!typeof(IRestEaseReplicaClient).IsAssignableFrom(clientType))
                throw Errors.MustImplement<IRestEaseReplicaClient>(clientType, nameof(clientType));
            clientName ??= clientType.FullName;

            object Factory(IServiceProvider c)
            {
                // 1. Validate type
                var interceptor = c.GetRequiredService<ReplicaClientInterceptor>();
                interceptor.ValidateType(clientType);

                // 2. Create REST client (of clientType)
                var httpClientFactory = c.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient(clientName);
                var client = new RestClient(httpClient) {
                    RequestBodySerializer = c.GetRequiredService<RequestBodySerializer>(),
                    ResponseDeserializer = c.GetRequiredService<ResponseDeserializer>()
                }.For(clientType);

                // 3. Create proxy mapping client to serviceType
                if (clientType != serviceType) {
                    var serviceProxyGenerator = c.GetRequiredService<IInterfaceCastProxyGenerator>();
                    var serviceProxyType = serviceProxyGenerator.GetProxyType(serviceType);
                    var serviceInterceptors = c.GetRequiredService<InterfaceCastInterceptor[]>();
                    client = serviceProxyType.CreateInstance(serviceInterceptors, client);
                }

                // 4. Create Replica Client
                var replicaProxyGenerator = c.GetRequiredService<IReplicaClientProxyGenerator>();
                var replicaProxyType = replicaProxyGenerator.GetProxyType(serviceType);
                var replicaInterceptors = c.GetRequiredService<ReplicaClientInterceptor[]>();
                client = replicaProxyType.CreateInstance(replicaInterceptors, client);
                return client;
            }

            Services.TryAddSingleton(serviceType, Factory);
            return this;
        }
    }
}
