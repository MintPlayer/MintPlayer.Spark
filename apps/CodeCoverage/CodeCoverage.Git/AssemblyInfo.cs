using MintPlayer.SourceGenerators.Attributes;

// The merge queue's conflict resolvers and validators ([Register], MergeQueue/Resolution/). The generated method has a name
// of its own, so it cannot collide with a referencing assembly's; MergeQueueResolutionServiceCollectionExtensions wraps it.
[assembly: ServiceRegistrationConfiguration(DefaultMethodName = "AddMergeQueueResolutionServices", DefaultAccessibility = EGeneratedAccessibility.Public)]
