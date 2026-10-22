using MintPlayer.Entities;
using MintPlayer.Spark;

namespace MintPlayer.Indexes;

/// <summary>
/// Like and dislike totals per subject. A <see cref="UserLike"/> holds one user's liked and disliked subject ids, so
/// the totals need a fan-out map-reduce: every array element emits one row, and the reduce sums them per subject.
/// </summary>
/// <remarks>
/// Eventually consistent like any index: a count read right after a like can lag, so the like endpoint waits for
/// non-stale results before it returns the totals.
/// </remarks>
public class Likes_Count : SparkMultiMapIndexCreationTask<LikeCount>
{
    public Likes_Count()
    {
        AddMap<UserLike>(userLikes => from u in userLikes
                                      from subjectId in u.Likes
                                      select new LikeCount { SubjectId = subjectId, Likes = 1, Dislikes = 0 });
        AddMap<UserLike>(userLikes => from u in userLikes
                                      from subjectId in u.Dislikes
                                      select new LikeCount { SubjectId = subjectId, Likes = 0, Dislikes = 1 });

        Reduce = results => from r in results
                            group r by r.SubjectId into g
                            select new LikeCount
                            {
                                SubjectId = g.Key,
                                Likes = g.Sum(x => x.Likes),
                                Dislikes = g.Sum(x => x.Dislikes),
                            };
    }
}

/// <summary>The totals of one subject, the reduce result of <see cref="Likes_Count"/>.</summary>
public class LikeCount
{
    public string SubjectId { get; set; } = string.Empty;
    public int Likes { get; set; }
    public int Dislikes { get; set; }
}
