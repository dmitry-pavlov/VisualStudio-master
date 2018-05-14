﻿using System;
using GitHub.Collections;

namespace GitHub.Models
{
    public interface IBranch : ICopyable<IBranch>,
        IEquatable<IBranch>, IComparable<IBranch>
    {
        string Id { get; }
        string Name { get; }
        IRepositoryModel Repository { get; }
        bool IsTracking { get; }
        string DisplayName { get; set; }
        string Sha { get; }
        string TrackedSha { get; }
    }
}
