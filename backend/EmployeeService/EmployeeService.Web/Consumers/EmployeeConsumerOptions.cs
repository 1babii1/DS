using Shared.Kafka;

namespace EmployeeService.Web.Consumers;

public class EmployeeConsumerOptions : KafkaConsumerOptions
{
    public EmployeeConsumerOptions() => GroupId = "employee-service";
}
