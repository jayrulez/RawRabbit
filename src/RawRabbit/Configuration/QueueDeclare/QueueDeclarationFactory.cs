﻿using System.Collections.Generic;
using RawRabbit.Common;

namespace RawRabbit.Configuration.Queue
{
	public interface IQueueConfigurationFactory
	{
		QueueDeclaration Create(string queueName);
		QueueDeclaration Create<TMessageType>();
	}

	public class QueueDeclarationFactory : IQueueConfigurationFactory
	{
		private readonly RawRabbitConfiguration _config;
		private readonly INamingConventions _conventions;

		public QueueDeclarationFactory(RawRabbitConfiguration config, INamingConventions conventions)
		{
			_config = config;
			_conventions = conventions;
		}

		public QueueDeclaration Create(string queueName)
		{
			return new QueueDeclaration
			{
				AutoDelete = _config.Queue.AutoDelete,
				Durable = _config.Queue.Durable,
				Exclusive = _config.Queue.Exclusive,
				Name = queueName,
				Arguments = new Dictionary<string, object>()
			};
		}

		public QueueDeclaration Create<TMessageType>()
		{
			var queueName = _conventions.QueueNamingConvention(typeof(TMessageType));
			return Create(queueName);
		}
	}
}
