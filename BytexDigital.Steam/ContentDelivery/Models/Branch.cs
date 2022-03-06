using System;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class Branch
    {
        public string Name { get; }
        public ulong BuildId { get; }
        public string Description { get; }
        public bool RequiresPassword { get; }
        public DateTimeOffset TimeUpdated { get; }

        public Branch(string name, ulong buildId, string description, bool requiresPassword, DateTimeOffset timeUpdated)
        {
            Name = name;
            BuildId = buildId;
            Description = description;
            RequiresPassword = requiresPassword;
            TimeUpdated = timeUpdated;
        }
    }
}