using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Library.Messages;

[MessageQueue("CompanyEvents")]
public record CompanyUpdatedMessage(string CompanyId, string CompanyName, List<string> EmployeeIds);
