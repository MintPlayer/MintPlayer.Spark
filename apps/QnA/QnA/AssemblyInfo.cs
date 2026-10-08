using MintPlayer.AspNetCore.Endpoints;

// The application's own generator endpoints: the E2E test seams (Testing/QnATestSeamEndpoints.cs),
// mapped only when their group's IsEnabled says so.
[assembly: EndpointsMethodName("MapQnAEndpoints")]
