using System;
using System.Collections.Generic;
using System.Text;

namespace StayRate.Domain.Common;

    public sealed class DomainRuleViolation : Exception
    {
        public DomainRuleViolation(string message) : base(message) { }
    }
