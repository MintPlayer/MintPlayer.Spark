using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// O4 of <c>docs/coverage_merge_queue_PRD.md</c>: synchronize keeps <c>translations.json</c> in ordinal key
/// order, so a seeded key lands among its siblings instead of at the end of its object.
/// </summary>
/// <remarks>
/// <para>
/// Why: appended keys made every two model PRs conflict at the same place, the end of the file. In sorted
/// position two PRs that add different entities touch different lines, and git merges them.
/// </para>
/// <para>
/// Red before the change: the seeder appended (<c>model.Beta</c> after <c>model.Gamma</c>), and a file with
/// nothing to seed was never rewritten, so an unsorted file stayed unsorted.
/// </para>
/// </remarks>
public sealed class TranslationsSeederOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spark-seed-order-" + Guid.NewGuid().ToString("N"));

    public TranslationsSeederOrderTests() => Directory.CreateDirectory(Path.Combine(_root, "App_Data"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_root, "App_Data", "translations.json");

    /// <summary>Writes the lines with CRLF and a trailing newline, the form every app's file is in.</summary>
    private void Write(params string[] lines) => File.WriteAllText(FilePath, Crlf(lines));

    private static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private int Seed(params (string Key, string Text)[] seeds) => TranslationsSeeder.Apply(_root, seeds);

    [Fact]
    public void A_new_key_is_inserted_at_its_sorted_position()
    {
        Write(
            "{",
            "  \"model\": {",
            "    \"Alpha\": {",
            "      \"en\": \"Alpha\"",
            "    },",
            "    \"Gamma\": {",
            "      \"en\": \"Gamma\"",
            "    }",
            "  }",
            "}");

        Seed(("model.Beta", "Beta")).Should().Be(1);

        File.ReadAllText(FilePath).Should().Be(Crlf(
            "{",
            "  \"model\": {",
            "    \"Alpha\": {",
            "      \"en\": \"Alpha\"",
            "    },",
            "    \"Beta\": {",
            "      \"en\": \"Beta\"",
            "    },",
            "    \"Gamma\": {",
            "      \"en\": \"Gamma\"",
            "    }",
            "  }",
            "}"));
    }

    [Fact]
    public void A_nested_key_is_inserted_sorted_at_every_level_it_creates_or_enters()
    {
        Write(
            "{",
            "  \"model\": {",
            "    \"Car\": {",
            "      \"attributes\": {",
            "        \"Brand\": {",
            "          \"label\": {",
            "            \"en\": \"Brand\"",
            "          }",
            "        },",
            "        \"Year\": {",
            "          \"label\": {",
            "            \"en\": \"Year\"",
            "          }",
            "        }",
            "      }",
            "    }",
            "  }",
            "}");

        Seed(("model.Car.attributes.Model.description", "The model name."),
             ("model.Car.attributes.Brand.description", "The make.")).Should().Be(2);

        File.ReadAllText(FilePath).Should().Be(Crlf(
            "{",
            "  \"model\": {",
            "    \"Car\": {",
            "      \"attributes\": {",
            "        \"Brand\": {",
            "          \"description\": {",
            "            \"en\": \"The make.\"",
            "          },",
            "          \"label\": {",
            "            \"en\": \"Brand\"",
            "          }",
            "        },",
            "        \"Model\": {",
            "          \"description\": {",
            "            \"en\": \"The model name.\"",
            "          }",
            "        },",
            "        \"Year\": {",
            "          \"label\": {",
            "            \"en\": \"Year\"",
            "          }",
            "        }",
            "      }",
            "    }",
            "  }",
            "}"));
    }

    [Fact]
    public void Order_is_ordinal_and_schema_stays_first()
    {
        Write(
            "{",
            "  \"$schema\": \"https://example.invalid/translations.schema.json\",",
            "  \"queries\": {",
            "    \"people\": {",
            "      \"nl\": \"Personen\",",
            "      \"en\": \"People\"",
            "    }",
            "  },",
            "  \"app\": {",
            "    \"title\": {",
            "      \"en\": \"Title\"",
            "    },",
            "    \"Title\": {",
            "      \"en\": \"Upper\"",
            "    }",
            "  }",
            "}");

        Seed().Should().Be(0, "nothing was seeded; the file is only put in order");

        File.ReadAllText(FilePath).Should().Be(Crlf(
            "{",
            "  \"$schema\": \"https://example.invalid/translations.schema.json\",",
            "  \"app\": {",
            "    \"Title\": {",
            "      \"en\": \"Upper\"",
            "    },",
            "    \"title\": {",
            "      \"en\": \"Title\"",
            "    }",
            "  },",
            "  \"queries\": {",
            "    \"people\": {",
            "      \"en\": \"People\",",
            "      \"nl\": \"Personen\"",
            "    }",
            "  }",
            "}"));
    }

    [Fact]
    public void A_second_run_is_byte_identical()
    {
        Write(
            "{",
            "  \"model\": {",
            "    \"Zulu\": {",
            "      \"en\": \"Zulu\"",
            "    },",
            "    \"Alpha\": {",
            "      \"en\": \"Alpha\"",
            "    }",
            "  }",
            "}");

        Seed(("model.Mike", "Mike"));
        var first = File.ReadAllBytes(FilePath);
        var firstWrite = File.GetLastWriteTimeUtc(FilePath);

        Seed(("model.Mike", "Mike")).Should().Be(0);

        File.ReadAllBytes(FilePath).Should().Equal(first);
        File.GetLastWriteTimeUtc(FilePath).Should().Be(firstWrite, "a sorted file with nothing to seed is not rewritten");
        first[0].Should().Be((byte)'{', "Spark writes no byte-order mark");
    }

    [Fact]
    public void A_sorted_file_in_another_layout_is_left_alone_when_there_is_nothing_to_seed()
    {
        // Sorting must not turn every synchronize into a reformat: the rewrite happens only to add a seed
        // or to restore the order, as before (S10 of #467).
        const string fourSpaces = "{\n    \"app\": {\n        \"title\": {\n            \"en\": \"T\"\n        }\n    }\n}\n";
        File.WriteAllText(FilePath, fourSpaces);

        Seed().Should().Be(0);

        File.ReadAllText(FilePath).Should().Be(fourSpaces);
    }
}
