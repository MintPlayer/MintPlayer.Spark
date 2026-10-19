using MintPlayer.Spark.Abstractions;

// The TypeScript contracts of Spark's core wire types (commerce X20 item 2, docs/guide-typescript-contracts.md).
// MintPlayer.Spark.Model shares this namespace, so its types land in the same module.
[assembly: SparkClientModule("MintPlayer.Spark.Abstractions", "@mintplayer/ng-spark/models")]

// TranslatedString is written by its own converter ({"entries":[...]} or a key); its TypeScript is hand-written.
[assembly: SparkClientType(typeof(TranslatedString), "TranslatedString", "translated-string")]
