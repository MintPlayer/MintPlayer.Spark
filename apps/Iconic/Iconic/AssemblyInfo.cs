using System.Runtime.CompilerServices;
using MintPlayer.AspNetCore.Endpoints;

[assembly: InternalsVisibleTo("Iconic.Tests")]

// The application's own generator endpoints (Endpoints/IconicEndpoints.cs).
[assembly: EndpointsMethodName("MapIconicEndpoints")]
