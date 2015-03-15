// Copyright 2007-2014 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.SubscriptionConnectors
{
    using System;
    using Pipeline;


    /// <summary>
    /// Connects a message handler to the ConsumePipe
    /// </summary>
    /// <typeparam name="T">The message type</typeparam>
    public interface HandlerConnector<T>
        where T : class
    {
        /// <summary>
        /// Connect a message handler for all messages of type T 
        /// </summary>
        /// <param name="consumePipe"></param>
        /// <param name="handler"></param>
        /// <param name="filters"></param>
        /// <returns></returns>
        ConnectHandle Connect(IConsumePipe consumePipe, MessageHandler<T> handler,
            params IFilter<ConsumeContext<T>>[] filters);

        /// <summary>
        /// Connect a message handler for messages with the specified RequestId
        /// </summary>
        /// <param name="consumePipe"></param>
        /// <param name="requestId"></param>
        /// <param name="handler"></param>
        /// <param name="filters"></param>
        /// <returns></returns>
        ConnectHandle Connect(IConsumePipe consumePipe, Guid requestId, MessageHandler<T> handler,
            params IFilter<ConsumeContext<T>>[] filters);
    }
}