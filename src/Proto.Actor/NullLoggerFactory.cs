﻿// -----------------------------------------------------------------------
//   <copyright file="NullLoggerFactory.cs" company="Asynkron AB">
//       Copyright (C) 2015-2020 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Proto
{
    public class NullLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string name) => NullLogger.Instance;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}