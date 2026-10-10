// One Spark host boots at a time: Spark's registries are process-wide (see SeoTesterWebAppFactory).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
