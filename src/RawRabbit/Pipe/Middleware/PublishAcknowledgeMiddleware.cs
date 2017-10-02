﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RawRabbit.Common;
using RawRabbit.Exceptions;
using RawRabbit.Logging;

namespace RawRabbit.Pipe.Middleware
{
	public class PublishAcknowledgeOptions
	{
		public Func<IPipeContext, TimeSpan> TimeOutFunc { get; set; }
		public Func<IPipeContext, IModel> ChannelFunc { get; set; }
		public Func<IPipeContext, bool> EnabledFunc { get; set; }
	}

	public class PublishAcknowledgeMiddleware : Middleware
	{
		private readonly IExclusiveLock _exclusive;
		private readonly ILog _logger = LogProvider.For<PublishAcknowledgeMiddleware>();
		protected Func<IPipeContext, TimeSpan> TimeOutFunc;
		protected Func<IPipeContext, IModel> ChannelFunc;
		protected Func<IPipeContext, bool> EnabledFunc;
		protected static ConcurrentDictionary<IModel, ConcurrentDictionary<ulong, TaskCompletionSource<ulong>>> ConfirmsDictionary = new ConcurrentDictionary<IModel, ConcurrentDictionary<ulong, TaskCompletionSource<ulong>>>();
		protected static ConcurrentDictionary<IModel, ulong> ChannelSequences = new ConcurrentDictionary<IModel, ulong>();

		public PublishAcknowledgeMiddleware(IExclusiveLock exclusive, PublishAcknowledgeOptions options = null)
		{
			_exclusive = exclusive;
			TimeOutFunc = options?.TimeOutFunc ?? (context => context.GetPublishAcknowledgeTimeout());
			ChannelFunc = options?.ChannelFunc ?? (context => context.GetTransientChannel());
			EnabledFunc = options?.EnabledFunc ?? (context => context.GetPublishAcknowledgeTimeout() != TimeSpan.MaxValue);
		}

		public override async Task InvokeAsync(IPipeContext context, CancellationToken token)
		{
			var enabled = GetEnabled(context);
			if (!enabled)
			{
				_logger.Debug("Publish Acknowledgement is disabled.");
				await Next.InvokeAsync(context, token);
				return;
			}

			var channel = GetChannel(context);
			if (!PublishAcknowledgeEnabled(channel))
			{
				EnableAcknowledgement(channel, token);
			}

			var ackTcs = new TaskCompletionSource<ulong>();
			var sequence = GetNextSequence(channel);
			SetupTimeout(context, sequence, ackTcs);
			if (!GetChannelDictionary(channel).TryAdd(sequence, ackTcs))
			{
				_logger.Info("Unable to add ack '{publishSequence}' on channel {channelNumber}", sequence, channel.ChannelNumber);
			};

			await Next.InvokeAsync(context, token);
			await ackTcs.Task;
		}

		protected virtual ulong GetNextSequence(IModel channel)
		{
			return ChannelSequences.AddOrUpdate(
				channel,
				c => c.NextPublishSeqNo,
				(c, seq) =>
				{
					if (c.NextPublishSeqNo == 1 && seq != 2)
					{
						return c.NextPublishSeqNo;
					}
					seq++;
					return seq > c.NextPublishSeqNo
						? seq
						: c.NextPublishSeqNo;
				});
		}

		protected virtual TimeSpan GetAcknowledgeTimeOut(IPipeContext context)
		{
			return TimeOutFunc(context);
		}

		protected virtual bool PublishAcknowledgeEnabled(IModel channel)
		{
			return channel.NextPublishSeqNo != 0UL;
		}

		protected virtual IModel GetChannel(IPipeContext context)
		{
			return ChannelFunc(context);
		}

		protected virtual bool GetEnabled(IPipeContext context)
		{
			return EnabledFunc(context);
		}

		protected virtual ConcurrentDictionary<ulong, TaskCompletionSource<ulong>> GetChannelDictionary(IModel channel)
		{
			return ConfirmsDictionary.GetOrAdd(
				key: channel,
				valueFactory: c => new ConcurrentDictionary<ulong, TaskCompletionSource<ulong>>());
		}

		protected virtual void EnableAcknowledgement(IModel channel, CancellationToken token)
		{
			_logger.Info("Setting 'Publish Acknowledge' for channel '{channelNumber}'", channel.ChannelNumber);
			_exclusive.Execute(channel, c =>
			{
				if (PublishAcknowledgeEnabled(c))
				{
					return;
				}
				c.ConfirmSelect();
				var dictionary = GetChannelDictionary(c);
				c.BasicAcks += (sender, args) =>
				{
					Task.Run(() =>
					{
						if (args.Multiple)
						{
							foreach (var deliveryTag in dictionary.Keys.Where(k => k <= args.DeliveryTag).ToList())
							{
								TaskCompletionSource<ulong> tcs;
								if (!dictionary.TryRemove(deliveryTag, out tcs))
								{
									continue;
								}
								if (!tcs.TrySetResult(deliveryTag))
								{
									continue;
								}
							}
						}
						else
						{
							TaskCompletionSource<ulong> tcs;
							dictionary.TryRemove(args.DeliveryTag, out tcs);
							tcs?.TrySetResult(args.DeliveryTag);
						}
					}, token);
				};
			}, token);
		}

		protected virtual void SetupTimeout(IPipeContext context, ulong sequence, TaskCompletionSource<ulong> ackTcs)
		{
			var timeout = GetAcknowledgeTimeOut(context);
			Timer ackTimer = null;
			_logger.Info("Setting up publish acknowledgement for {publishSequence} with timeout {timeout:g}", sequence, timeout);
			ackTimer = new Timer(state =>
			{
				ackTcs.TrySetException(
					new PublishConfirmException($"The broker did not send a publish acknowledgement for message {sequence} within {timeout:g}."));
				ackTimer?.Dispose();
			}, null, timeout, new TimeSpan(-1));
		}
	}

	public static class PublishAcknowledgePipeExtensions
	{
		public static IPipeContext UsePublishAcknowledgeTimeout(this IPipeContext context, TimeSpan timeout)
		{
			context.Properties.TryAdd(PipeKey.PublishAcknowledgeTimeout, timeout);
			return context;
		}

		public static IPipeContext UsePublishAcknowledge(this IPipeContext context, bool use = true)
		{
			return !use
				? context.UsePublishAcknowledgeTimeout(TimeSpan.MaxValue)
				: context;
		}

		public static TimeSpan GetPublishAcknowledgeTimeout(this IPipeContext context)
		{
			var fallback = context.GetClientConfiguration().PublishConfirmTimeout;
			return context.Get(PipeKey.PublishAcknowledgeTimeout, fallback);
		}
	}
}
