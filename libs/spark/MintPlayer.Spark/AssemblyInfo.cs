using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

[assembly: ServiceRegistrationConfiguration(DefaultMethodName = "AddSparkServices")]
[assembly: EndpointsMethodName("MapSparkCoreEndpoints")]
