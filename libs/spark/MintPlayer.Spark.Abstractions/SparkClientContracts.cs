using MintPlayer.Spark.Abstractions;

// The TypeScript contracts of Spark's core wire types (commerce X20 item 2, docs/guide-typescript-contracts.md).
// MintPlayer.Spark.Model shares this namespace, so its types land in the same module.
[assembly: SparkClientModule("MintPlayer.Spark.Abstractions", "@mintplayer/ng-spark/models")]

// The consent primitive (commerce X21) and embed mode (X23) have entry points of their own.
[assembly: SparkClientModule("MintPlayer.Spark.Abstractions.Consent", "@mintplayer/ng-spark/consent")]
[assembly: SparkClientModule("MintPlayer.Spark.Abstractions.Embed", "@mintplayer/ng-spark/embed")]

// TranslatedString is written by its own converter ({"entries":[...]} or a key); its TypeScript is hand-written.
[assembly: SparkClientType(typeof(TranslatedString), "TranslatedString", "translated-string")]

// Money (X4) is written by MoneyJsonConverter ({"amount":12.30,"currency":"EUR"}); its TypeScript is hand-written in
// models/src/money.ts. Declared here so commerce contracts can carry prices (commerce M4).
[assembly: SparkClientType(typeof(Money), "Money", "money")]

// Quantity (warehouse WX8) is written by QuantityJsonConverter ({"Amount":12,"Unit":"EA"}); its TypeScript is hand-written
// in models/src/quantity.ts.
[assembly: SparkClientType(typeof(Quantity), "Quantity", "quantity")]
