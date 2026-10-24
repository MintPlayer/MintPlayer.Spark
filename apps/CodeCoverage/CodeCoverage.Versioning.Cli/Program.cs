using CodeCoverage.Versioning.Api;

return args switch
{
	["api-list", var path] => ApiList(path),
	_ => Usage(),
};

static int ApiList(string path)
{
	using var stream = File.OpenRead(path);
	var lines = DotnetApiListing.List(stream);
	foreach (var line in lines) Console.WriteLine(line);
	Console.WriteLine(DotnetApiListing.Hash(lines));
	return 0;
}

static int Usage()
{
	Console.Error.WriteLine("usage: codecoverage-versioning api-list <assembly.dll>");
	return 2;
}
