using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Health
{
    public sealed record HealthMessage
    {
        public required string PodId { get; init; }
        public DateTimeOffset Timestamp { get; set; }
    }
}
