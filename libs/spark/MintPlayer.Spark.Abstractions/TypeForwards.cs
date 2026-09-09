// The model attributes moved to MintPlayer.Spark.Attributes. The namespace did not change, so source
// consumers never noticed -- but these attributes are read by *runtime* reflection
// (ModelSynchronizer, ReferenceResolver), so an assembly compiled against the old layout would not
// fail loudly against this one: it would silently produce a wrong model, with properties the
// framework simply stops seeing. Twelve one-line forwards remove that entirely.
//
// Preview-grade packages may take breaking changes, and this is not an exception to that -- it is a
// break a consumer cannot see, which is a different thing.

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.BreadcrumbAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.DefaultIndexAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.FromIndexAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.GenerateIndexAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.IgnoreForIndexAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.IgnorePropertyAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.LookupReferenceAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.ReferenceAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.SearchAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.SortableAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.SparkAttributeDescriptionAttribute))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(MintPlayer.Spark.Abstractions.SparkTranslationsAttribute))]
