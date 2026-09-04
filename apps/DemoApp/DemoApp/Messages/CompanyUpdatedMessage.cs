using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Messages;

[MessageQueue("CompanyEvents")]
public record CompanyUpdatedMessage(string CompanyId, string CompanyName, List<string> EmployeeIds);
