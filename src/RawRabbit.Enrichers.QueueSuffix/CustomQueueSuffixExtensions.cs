﻿using RawRabbit.Pipe;

namespace RawRabbit.Enrichers.QueueSuffix
{
	public static class CustomQueueSuffixExtensions
	{
		private const string CustomQueueSuffix = "CustomQueueSuffix";
		private const string CustomQueueSuffixActivated = "CustomQueueSuffixActivated";

		public static IPipeContext UseCustomQueueSuffix(this IPipeContext context, string prefix)
		{
			context.Properties.AddOrReplace(CustomQueueSuffix, prefix);
			return context;
		}

		public static IPipeContext UseCustomQueueSuffix(this IPipeContext context, bool activated)
		{
			context.Properties.TryAdd(CustomQueueSuffixActivated, activated);
			return context;
		}

		public static string GetCustomQueueSuffix(this IPipeContext context)
		{
			return context.Get<string>(CustomQueueSuffix);
		}

		public static bool GetCustomQueueSuffixActivated(this IPipeContext context)
		{
			return context.Get(CustomQueueSuffixActivated, true);
		}
	}
}
