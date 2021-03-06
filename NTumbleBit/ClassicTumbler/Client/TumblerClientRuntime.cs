﻿using System;
using System.Collections.Generic;
using System.Text;
using NBitcoin;
using System.IO;
using NBitcoin.RPC;
using NTumbleBit.Logging;
using Microsoft.Extensions.Logging;
using NTumbleBit.ClassicTumbler;
using System.Net;
using System.Threading.Tasks;
using System.Net.Http;
using NTumbleBit.Services;
using NTumbleBit.Configuration;
using NTumbleBit.ClassicTumbler.Client.ConnectionSettings;
using NTumbleBit.ClassicTumbler.CLI;

namespace NTumbleBit.ClassicTumbler.Client
{
	public enum Identity
	{
		Alice,
		Bob
	}

	public class TumblerClientRuntime : IDisposable
	{
		public static TumblerClientRuntime FromConfiguration(TumblerClientConfiguration configuration, ClientInteraction interaction)
		{
			return FromConfigurationAsync(configuration, interaction).GetAwaiter().GetResult();
		}
		public static async Task<TumblerClientRuntime> FromConfigurationAsync(TumblerClientConfiguration configuration, ClientInteraction interaction)
		{
			interaction = interaction ?? new AcceptAllClientInteraction();
			var runtime = new TumblerClientRuntime();
			try
			{
				runtime.Network = configuration.Network;
				runtime.TumblerServer = configuration.TumblerServer;
				runtime.BobSettings = configuration.BobConnectionSettings;
				runtime.AliceSettings = configuration.AliceConnectionSettings;

				var torOnly = runtime.AliceSettings is TorConnectionSettings && runtime.BobSettings is TorConnectionSettings;

				await runtime.SetupTorAsync(interaction).ConfigureAwait(false);
				if(torOnly)
					Logs.Configuration.LogInformation("Successfully authenticated to Tor");

				RPCClient rpc = null;
				try
				{
					rpc = configuration.RPCArgs.ConfigureRPCClient(configuration.Network);
				}
				catch
				{
					throw new ConfigException("Please, fix rpc settings in " + configuration.ConfigurationFile);
				}

				var dbreeze = new DBreezeRepository(Path.Combine(configuration.DataDir, "db2"));
				runtime.Cooperative = configuration.Cooperative;
				runtime.Repository = dbreeze;
				runtime._Disposables.Add(dbreeze);
				runtime.Tracker = new Tracker(dbreeze, runtime.Network);
				runtime.Services = ExternalServices.CreateFromRPCClient(rpc, dbreeze, runtime.Tracker);

				if(configuration.OutputWallet.RootKey != null && configuration.OutputWallet.KeyPath != null)
					runtime.DestinationWallet = new ClientDestinationWallet(configuration.OutputWallet.RootKey, configuration.OutputWallet.KeyPath, dbreeze, configuration.Network);
				else if(configuration.OutputWallet.RPCArgs != null)
				{
					try
					{
						runtime.DestinationWallet = new RPCDestinationWallet(configuration.OutputWallet.RPCArgs.ConfigureRPCClient(runtime.Network));
					}
					catch
					{
						throw new ConfigException("Please, fix outputwallet rpc settings in " + configuration.ConfigurationFile);
					}
				}
				else
					throw new ConfigException("Missing configuration for outputwallet");

				runtime.TumblerParameters = dbreeze.Get<ClassicTumbler.ClassicTumblerParameters>("Configuration", configuration.TumblerServer.AbsoluteUri);
				var parameterHash = ClassicTumbler.ClassicTumblerParameters.ExtractHashFromUrl(configuration.TumblerServer);

				if(runtime.TumblerParameters != null && runtime.TumblerParameters.GetHash() != parameterHash)
					runtime.TumblerParameters = null;

				if(!configuration.OnlyMonitor)
				{
					if(!torOnly && configuration.CheckIp)
					{
						var ip1 = GetExternalIp(runtime.CreateTumblerClient(0, Identity.Alice), "https://myexternalip.com/raw");
						var ip2 = GetExternalIp(runtime.CreateTumblerClient(0, Identity.Bob), "https://icanhazip.com/");
						var aliceIp = ip1.GetAwaiter().GetResult();
						var bobIp = ip2.GetAwaiter().GetResult();
						if(aliceIp.Equals(bobIp))
						{
							var error = "Same IP detected for Bob and Alice, the tumbler can link input address to output address";

							if(configuration.AllowInsecure)
							{
								Logs.Configuration.LogWarning(error);
							}
							else
							{
								throw new ConfigException(error + ", use parameter -allowinsecure or allowinsecure=true in config file to ignore.");
							}
						}
						else
							Logs.Configuration.LogInformation("Alice and Bob have different IP configured");
					}


					var client = runtime.CreateTumblerClient(0);
					Logs.Configuration.LogInformation("Downloading tumbler information of " + configuration.TumblerServer.AbsoluteUri);
					if(runtime.TumblerParameters == null)
					{
						var parameters = Retry(3, () => client.GetTumblerParameters());
						if(parameters == null)
							throw new ConfigException("Unable to download tumbler's parameters");

						await interaction.ConfirmParametersAsync(parameters).ConfigureAwait(false);
						runtime.Repository.UpdateOrInsert("Configuration", runtime.TumblerServer.AbsoluteUri, parameters, (o, n) => n);
						runtime.TumblerParameters = parameters;

						if(parameters.GetHash() != parameterHash)
							throw new ConfigException("The tumbler returned an invalid configuration");

						Logs.Configuration.LogInformation("Tumbler parameters saved");
					}
				}
			}
			catch
			{
				runtime.Dispose();
				throw;
			}
			return runtime;
		}

		private async Task SetupTorAsync(ClientInteraction interaction)
		{
			await SetupTorAsync(interaction, AliceSettings).ConfigureAwait(false);
			await SetupTorAsync(interaction, BobSettings).ConfigureAwait(false);
		}

		private Task SetupTorAsync(ClientInteraction interaction, ConnectionSettingsBase settings)
		{
			var tor = settings as TorConnectionSettings;
			if(tor == null)
				return Task.CompletedTask;
			return tor.SetupAsync(interaction);
		}

		private static async Task<IPAddress> GetExternalIp(TumblerClient client, string url)
		{
			var result = await client.Client.GetAsync(url).ConfigureAwait(false);
			var content = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
			return IPAddress.Parse(content.Replace("\n", string.Empty));
		}

		public BroadcasterJob CreateBroadcasterJob()
		{
			return new BroadcasterJob(Services);
		}

		public ConnectionSettingsBase BobSettings
		{
			get; set;
		}

		public ConnectionSettingsBase AliceSettings
		{
			get; set;
		}

		public bool Cooperative
		{
			get; set;
		}

		public Uri TumblerServer
		{
			get; set;
		}

		public TumblerClient CreateTumblerClient(int cycle, Identity? identity = null)
		{
			if(identity == null)
				identity = RandomUtils.GetUInt32() % 2 == 0 ? Identity.Alice : Identity.Bob;
			return CreateTumblerClient(cycle, identity == Identity.Alice ? AliceSettings : BobSettings);
		}

		private TumblerClient CreateTumblerClient(int cycleId, ConnectionSettingsBase settings)
		{
			var client = new TumblerClient(Network, TumblerServer, cycleId);
			var handler = settings.CreateHttpHandler();
			if(handler != null)
				client.SetHttpHandler(handler);
			return client;
		}

		public StateMachinesExecutor CreateStateMachineJob()
		{
			return new StateMachinesExecutor(this);
		}

		private static T Retry<T>(int count, Func<T> act)
		{
			var exceptions = new List<Exception>();
			for(int i = 0; i < count; i++)
			{
				try
				{
					return act();
				}
				catch(Exception ex)
				{
					exceptions.Add(ex);
				}
			}
			throw new AggregateException(exceptions);
		}

		List<IDisposable> _Disposables = new List<IDisposable>();

		public void Dispose()
		{
			foreach(var disposable in _Disposables)
				disposable.Dispose();
			_Disposables.Clear();
		}


		public IDestinationWallet DestinationWallet
		{
			get; set;
		}

		public Network Network
		{
			get;
			set;
		}
		public ExternalServices Services
		{
			get;
			set;
		}
		public Tracker Tracker
		{
			get;
			set;
		}
		public ClassicTumblerParameters TumblerParameters
		{
			get;
			set;
		}
		public DBreezeRepository Repository
		{
			get;
			set;
		}
	}
}
