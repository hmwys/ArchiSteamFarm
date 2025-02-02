//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2019 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class Statistics : IDisposable {
		private const byte MaxMatchedBotsHard = 40;
		private const byte MaxMatchedBotsSoft = 20;
		private const byte MaxMatchingRounds = 10;
		private const byte MinAnnouncementCheckTTL = 6; // Minimum amount of hours we must wait before checking eligibility for Announcement, should be lower than MinPersonaStateTTL
		private const byte MinHeartBeatTTL = 10; // Minimum amount of minutes we must wait before sending next HeartBeat
		private const byte MinItemsCount = 100; // Minimum amount of items to be eligible for public listing
		private const byte MinPersonaStateTTL = 8; // Minimum amount of hours we must wait before requesting persona state update
		private const string URL = "https://" + SharedInfo.StatisticsServer;

		private static readonly ImmutableHashSet<Steam.Asset.EType> AcceptedMatchableTypes = ImmutableHashSet.Create(
			Steam.Asset.EType.Emoticon,
			Steam.Asset.EType.FoilTradingCard,
			Steam.Asset.EType.ProfileBackground,
			Steam.Asset.EType.TradingCard
		);

		private readonly Bot Bot;
		private readonly SemaphoreSlim MatchActivelySemaphore = new SemaphoreSlim(1, 1);
		private readonly Timer MatchActivelyTimer;
		private readonly SemaphoreSlim RequestsSemaphore = new SemaphoreSlim(1, 1);

		private DateTime LastAnnouncementCheck;
		private DateTime LastHeartBeat;
		private DateTime LastPersonaStateRequest;
		private bool ShouldSendHeartBeats;

		internal Statistics([NotNull] Bot bot) {
			Bot = bot ?? throw new ArgumentNullException(nameof(bot));

			MatchActivelyTimer = new Timer(
				async e => await MatchActively().ConfigureAwait(false),
				null,
				TimeSpan.FromHours(1) + TimeSpan.FromSeconds(ASF.LoadBalancingDelay * Bot.Bots.Count), // Delay
				TimeSpan.FromHours(8) // Period
			);
		}

		public void Dispose() {
			MatchActivelySemaphore.Dispose();
			MatchActivelyTimer.Dispose();
			RequestsSemaphore.Dispose();
		}

		internal async Task OnHeartBeat() {
			// Request persona update if needed
			if ((DateTime.UtcNow > LastPersonaStateRequest.AddHours(MinPersonaStateTTL)) && (DateTime.UtcNow > LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL))) {
				LastPersonaStateRequest = DateTime.UtcNow;
				Bot.RequestPersonaStateUpdate();
			}

			if (!ShouldSendHeartBeats || (DateTime.UtcNow < LastHeartBeat.AddMinutes(MinHeartBeatTTL))) {
				return;
			}

			await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (!ShouldSendHeartBeats || (DateTime.UtcNow < LastHeartBeat.AddMinutes(MinHeartBeatTTL))) {
					return;
				}

				const string request = URL + "/Api/HeartBeat";

				Dictionary<string, string> data = new Dictionary<string, string>(2, StringComparer.Ordinal) {
					{ "Guid", ASF.GlobalDatabase.Guid.ToString("N") },
					{ "SteamID", Bot.SteamID.ToString() }
				};

				WebBrowser.BasicResponse response = await Bot.ArchiWebHandler.WebBrowser.UrlPost(request, data, requestOptions: WebBrowser.ERequestOptions.ReturnClientErrors).ConfigureAwait(false);

				if (response == null) {
					return;
				}

				if (response.StatusCode.IsClientErrorCode()) {
					LastHeartBeat = DateTime.MinValue;
					ShouldSendHeartBeats = false;

					return;
				}

				LastHeartBeat = DateTime.UtcNow;
			} finally {
				RequestsSemaphore.Release();
			}
		}

		internal async Task OnLoggedOn() {
			if (!await Bot.ArchiWebHandler.JoinGroup(SharedInfo.ASFGroupSteamID).ConfigureAwait(false)) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiWebHandler.JoinGroup)));
			}
		}

		internal async Task OnPersonaState(string nickname = null, string avatarHash = null) {
			if ((DateTime.UtcNow < LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL)) && (ShouldSendHeartBeats || (LastHeartBeat == DateTime.MinValue))) {
				return;
			}

			await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if ((DateTime.UtcNow < LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL)) && (ShouldSendHeartBeats || (LastHeartBeat == DateTime.MinValue))) {
					return;
				}

				// Don't announce if we don't meet conditions
				bool? eligible = await IsEligibleForListing().ConfigureAwait(false);

				if (!eligible.HasValue) {
					// This is actually network failure, so we'll stop sending heartbeats but not record it as valid check
					ShouldSendHeartBeats = false;

					return;
				}

				if (!eligible.Value) {
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = false;

					return;
				}

				string tradeToken = await Bot.ArchiHandler.GetTradeToken().ConfigureAwait(false);

				if (string.IsNullOrEmpty(tradeToken)) {
					// This is actually network failure, so we'll stop sending heartbeats but not record it as valid check
					ShouldSendHeartBeats = false;

					return;
				}

				HashSet<Steam.Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(type => AcceptedMatchableTypes.Contains(type)).ToHashSet();

				if (acceptedMatchableTypes.Count == 0) {
					Bot.ArchiLogger.LogNullError(nameof(acceptedMatchableTypes));
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = false;

					return;
				}

				HashSet<Steam.Asset> inventory = await Bot.ArchiWebHandler.GetInventory(tradable: true, wantedTypes: acceptedMatchableTypes).ConfigureAwait(false);

				if (inventory == null) {
					// This is actually inventory failure, so we'll stop sending heartbeats but not record it as valid check
					ShouldSendHeartBeats = false;

					return;
				}

				LastAnnouncementCheck = DateTime.UtcNow;

				// This is actual inventory
				if (inventory.Count < MinItemsCount) {
					ShouldSendHeartBeats = false;

					return;
				}

				const string request = URL + "/Api/Announce";

				Dictionary<string, string> data = new Dictionary<string, string>(9, StringComparer.Ordinal) {
					{ "AvatarHash", avatarHash ?? "" },
					{ "GamesCount", inventory.Select(item => item.RealAppID).Distinct().Count().ToString() },
					{ "Guid", ASF.GlobalDatabase.Guid.ToString("N") },
					{ "ItemsCount", inventory.Count.ToString() },
					{ "MatchableTypes", JsonConvert.SerializeObject(acceptedMatchableTypes) },
					{ "MatchEverything", Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) ? "1" : "0" },
					{ "Nickname", nickname ?? "" },
					{ "SteamID", Bot.SteamID.ToString() },
					{ "TradeToken", tradeToken }
				};

				WebBrowser.BasicResponse response = await Bot.ArchiWebHandler.WebBrowser.UrlPost(request, data, requestOptions: WebBrowser.ERequestOptions.ReturnClientErrors).ConfigureAwait(false);

				if (response == null) {
					return;
				}

				if (response.StatusCode.IsClientErrorCode()) {
					LastHeartBeat = DateTime.MinValue;
					ShouldSendHeartBeats = false;

					return;
				}

				LastHeartBeat = DateTime.UtcNow;
				ShouldSendHeartBeats = true;
			} finally {
				RequestsSemaphore.Release();
			}
		}

		[ItemCanBeNull]
		private async Task<ImmutableHashSet<ListedUser>> GetListedUsers() {
			const string request = URL + "/Api/Bots?matchEverything=1";

			WebBrowser.ObjectResponse<ImmutableHashSet<ListedUser>> objectResponse = await Bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<ImmutableHashSet<ListedUser>>(request).ConfigureAwait(false);

			return objectResponse?.Content;
		}

		private async Task<bool?> IsEligibleForListing() {
			bool? isEligibleForMatching = await IsEligibleForMatching().ConfigureAwait(false);

			if (isEligibleForMatching != true) {
				return isEligibleForMatching;
			}

			// Bot must have public inventory
			bool? hasPublicInventory = await Bot.ArchiWebHandler.HasPublicInventory().ConfigureAwait(false);

			if (hasPublicInventory != true) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.WarningFailedWithError, nameof(Bot.ArchiWebHandler.HasPublicInventory) + ": " + (hasPublicInventory?.ToString() ?? "null")));

				return hasPublicInventory;
			}

			return true;
		}

		private async Task<bool?> IsEligibleForMatching() {
			// Bot must have ASF 2FA
			if (!Bot.HasMobileAuthenticator) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.WarningFailedWithError, nameof(Bot.HasMobileAuthenticator) + ": " + Bot.HasMobileAuthenticator));

				return false;
			}

			// Bot must have STM enable in TradingPreferences
			if (!Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.SteamTradeMatcher)) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.WarningFailedWithError, nameof(Bot.BotConfig.TradingPreferences) + ": " + Bot.BotConfig.TradingPreferences));

				return false;
			}

			// Bot must have at least one accepted matchable type set
			if ((Bot.BotConfig.MatchableTypes.Count == 0) || Bot.BotConfig.MatchableTypes.All(type => !AcceptedMatchableTypes.Contains(type))) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.WarningFailedWithError, nameof(Bot.BotConfig.MatchableTypes) + ": " + Bot.BotConfig.MatchableTypes));

				return false;
			}

			// Bot must have valid API key (e.g. not being restricted account)
			bool? hasValidApiKey = await Bot.ArchiWebHandler.HasValidApiKey().ConfigureAwait(false);

			if (hasValidApiKey != true) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.WarningFailedWithError, nameof(Bot.ArchiWebHandler.HasValidApiKey) + ": " + (hasValidApiKey?.ToString() ?? "null")));

				return hasValidApiKey;
			}

			return true;
		}

		private async Task MatchActively() {
			if (!Bot.IsConnectedAndLoggedOn || Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) || !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively) || (await IsEligibleForMatching().ConfigureAwait(false) != true)) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

				return;
			}

			HashSet<Steam.Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(AcceptedMatchableTypes.Contains).ToHashSet();

			if (acceptedMatchableTypes.Count == 0) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

				return;
			}

			if (!await MatchActivelySemaphore.WaitAsync(0).ConfigureAwait(false)) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

				return;
			}

			try {
				Bot.ArchiLogger.LogGenericTrace(Strings.Starting);

				Dictionary<ulong, (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs)> triedSteamIDs = new Dictionary<ulong, (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs)>();

				bool match = true;

				for (byte i = 0; (i < MaxMatchingRounds) && match; i++) {
					if (i > 0) {
						// After each round we wait at least 5 minutes for all bots to react
						await Task.Delay(5 * 60 * 1000).ConfigureAwait(false);
					}

					if (!Bot.IsConnectedAndLoggedOn || Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) || !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively) || (await IsEligibleForMatching().ConfigureAwait(false) != true)) {
						Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

						break;
					}

					using (await Bot.Actions.GetTradingLock().ConfigureAwait(false)) {
						Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ActivelyMatchingItems, i));
						match = await MatchActivelyRound(acceptedMatchableTypes, triedSteamIDs).ConfigureAwait(false);
						Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.DoneActivelyMatchingItems, i));
					}
				}

				Bot.ArchiLogger.LogGenericTrace(Strings.Done);
			} finally {
				MatchActivelySemaphore.Release();
			}
		}

		[SuppressMessage("ReSharper", "FunctionComplexityOverflow")]
		private async Task<bool> MatchActivelyRound(IReadOnlyCollection<Steam.Asset.EType> acceptedMatchableTypes, IDictionary<ulong, (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs)> triedSteamIDs) {
			if ((acceptedMatchableTypes == null) || (acceptedMatchableTypes.Count == 0) || (triedSteamIDs == null)) {
				Bot.ArchiLogger.LogNullError(nameof(acceptedMatchableTypes) + " || " + nameof(triedSteamIDs));

				return false;
			}

			HashSet<Steam.Asset> ourInventory = await Bot.ArchiWebHandler.GetInventory(wantedTypes: acceptedMatchableTypes).ConfigureAwait(false);

			if ((ourInventory == null) || (ourInventory.Count == 0)) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(ourInventory)));

				return false;
			}

			(Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> fullState, Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> tradableState) = Trading.GetDividedInventoryState(ourInventory);

			if (Trading.IsEmptyForMatching(fullState, tradableState)) {
				// User doesn't have any more dupes in the inventory
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(fullState) + " || " + nameof(tradableState)));

				return false;
			}

			ImmutableHashSet<ListedUser> listedUsers = await GetListedUsers().ConfigureAwait(false);

			if ((listedUsers == null) || (listedUsers.Count == 0)) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(listedUsers)));

				return false;
			}

			byte emptyMatches = 0;
			HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)> skippedSetsThisRound = new HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)>();

			foreach (ListedUser listedUser in listedUsers.Where(listedUser => listedUser.MatchEverything && acceptedMatchableTypes.Any(listedUser.MatchableTypes.Contains) && (!triedSteamIDs.TryGetValue(listedUser.SteamID, out (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs) attempt) || (attempt.Tries < byte.MaxValue)) && !Bot.IsBlacklistedFromTrades(listedUser.SteamID)).OrderBy(listedUser => triedSteamIDs.TryGetValue(listedUser.SteamID, out (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs) attempt) ? attempt.Tries : 0).ThenByDescending(listedUser => listedUser.Score).Take(MaxMatchedBotsHard)) {
				HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)> wantedSets = tradableState.Keys.Where(set => !skippedSetsThisRound.Contains(set) && listedUser.MatchableTypes.Contains(set.Type)).ToHashSet();

				if (wantedSets.Count == 0) {
					continue;
				}

				Bot.ArchiLogger.LogGenericTrace(listedUser.SteamID + "...");

				HashSet<Steam.Asset> theirInventory = await Bot.ArchiWebHandler.GetInventory(listedUser.SteamID, tradable: true, wantedSets: wantedSets).ConfigureAwait(false);

				if ((theirInventory == null) || (theirInventory.Count == 0)) {
					Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(theirInventory)));

					continue;
				}

				HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)> skippedSetsThisUser = new HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)>();

				Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> theirTradableState = Trading.GetInventoryState(theirInventory);
				Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> inventoryStateChanges = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>>();

				for (byte i = 0; i < Trading.MaxTradesPerAccount; i++) {
					byte itemsInTrade = 0;
					HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)> skippedSetsThisTrade = new HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)>();

					Dictionary<ulong, uint> classIDsToGive = new Dictionary<ulong, uint>();
					Dictionary<ulong, uint> classIDsToReceive = new Dictionary<ulong, uint>();

					foreach (((uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) set, Dictionary<ulong, uint> ourFullItems) in fullState.Where(set => !skippedSetsThisUser.Contains(set.Key) && listedUser.MatchableTypes.Contains(set.Key.Type) && set.Value.Values.Any(count => count > 1))) {
						if (!tradableState.TryGetValue(set, out Dictionary<ulong, uint> ourTradableItems) || (ourTradableItems.Count == 0)) {
							continue;
						}

						if (!theirTradableState.TryGetValue(set, out Dictionary<ulong, uint> theirItems) || (theirItems.Count == 0)) {
							continue;
						}

						// Those 2 collections are on user-basis since we can't be sure that the trade passes through (and therefore we need to keep original state in case of failure)
						Dictionary<ulong, uint> ourFullSet = new Dictionary<ulong, uint>(ourFullItems);
						Dictionary<ulong, uint> ourTradableSet = new Dictionary<ulong, uint>(ourTradableItems);

						// We also have to take into account changes that happened in previoius trades with this user, so this block will adapt to that
						if (inventoryStateChanges.TryGetValue(set, out Dictionary<ulong, uint> pastChanges) && (pastChanges.Count > 0)) {
							foreach ((ulong classID, uint amount) in pastChanges) {
								if (!ourFullSet.TryGetValue(classID, out uint fullAmount) || (fullAmount == 0) || (fullAmount < amount)) {
									Bot.ArchiLogger.LogNullError(nameof(fullAmount));

									return false;
								}

								if (fullAmount > amount) {
									ourFullSet[classID] = fullAmount - amount;
								} else {
									ourFullSet.Remove(classID);
								}

								if (!ourTradableSet.TryGetValue(classID, out uint tradableAmount) || (tradableAmount == 0) || (tradableAmount < amount)) {
									Bot.ArchiLogger.LogNullError(nameof(tradableAmount));

									return false;
								}

								if (fullAmount > amount) {
									ourTradableSet[classID] = fullAmount - amount;
								} else {
									ourTradableSet.Remove(classID);
								}
							}

							if (Trading.IsEmptyForMatching(ourFullSet, ourTradableSet)) {
								continue;
							}
						}

						bool match;

						do {
							match = false;

							foreach ((ulong ourItem, uint ourAmount) in ourFullSet.Where(item => item.Value > 1).OrderByDescending(item => item.Value)) {
								if (!ourTradableSet.TryGetValue(ourItem, out uint tradableAmount) || (tradableAmount == 0)) {
									continue;
								}

								foreach ((ulong theirItem, _) in theirItems.OrderBy(item => ourFullSet.TryGetValue(item.Key, out uint ourAmountOfTheirItem) ? ourAmountOfTheirItem : 0)) {
									if (ourFullSet.TryGetValue(theirItem, out uint ourAmountOfTheirItem) && (ourAmount <= ourAmountOfTheirItem + 1)) {
										continue;
									}

									// Skip this set from the remaining of this round
									skippedSetsThisTrade.Add(set);

									// Update our state based on given items
									classIDsToGive[ourItem] = classIDsToGive.TryGetValue(ourItem, out uint givenAmount) ? givenAmount + 1 : 1;
									ourFullSet[ourItem] = ourAmount - 1; // We don't need to remove anything here because we can guarantee that ourItem.Value is at least 2

									if (inventoryStateChanges.TryGetValue(set, out Dictionary<ulong, uint> currentChanges)) {
										currentChanges[ourItem] = currentChanges.TryGetValue(ourItem, out uint amount) ? amount + 1 : 1;
									} else {
										inventoryStateChanges[set] = new Dictionary<ulong, uint> {
											{ ourItem, 1 }
										};
									}

									// Update our state based on received items
									classIDsToReceive[theirItem] = classIDsToReceive.TryGetValue(theirItem, out uint receivedAmount) ? receivedAmount + 1 : 1;
									ourFullSet[theirItem] = ourAmountOfTheirItem + 1;

									if (tradableAmount > 1) {
										ourTradableSet[ourItem] = tradableAmount - 1;
									} else {
										ourTradableSet.Remove(ourItem);
									}

									// Update their state based on taken items
									if (!theirItems.TryGetValue(theirItem, out uint theirAmount) || (theirAmount == 0)) {
										Bot.ArchiLogger.LogNullError(nameof(theirAmount));

										return false;
									}

									if (theirAmount > 1) {
										theirItems[theirItem] = theirAmount - 1;
									} else {
										theirItems.Remove(theirItem);
									}

									itemsInTrade += 2;

									match = true;

									break;
								}

								if (match) {
									break;
								}
							}
						} while (match && (itemsInTrade < Trading.MaxItemsPerTrade - 1));

						if (itemsInTrade >= Trading.MaxItemsPerTrade - 1) {
							break;
						}
					}

					if (skippedSetsThisTrade.Count == 0) {
						Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(skippedSetsThisTrade)));

						break;
					}

					HashSet<Steam.Asset> itemsToGive = Trading.GetTradableItemsFromInventory(ourInventory, classIDsToGive);
					HashSet<Steam.Asset> itemsToReceive = Trading.GetTradableItemsFromInventory(theirInventory, classIDsToReceive);

					if ((itemsToGive.Count != itemsToReceive.Count) || !Trading.IsFairExchange(itemsToGive, itemsToReceive)) {
						// Failsafe
						Bot.ArchiLogger.LogGenericError(string.Format(Strings.WarningFailedWithError, Strings.ErrorAborted));

						return false;
					}

					if (triedSteamIDs.TryGetValue(listedUser.SteamID, out (byte Tries, ISet<ulong> GivenAssetIDs, ISet<ulong> ReceivedAssetIDs) previousAttempt)) {
						if (itemsToGive.Select(item => item.AssetID).All(previousAttempt.GivenAssetIDs.Contains) && itemsToReceive.Select(item => item.AssetID).All(previousAttempt.ReceivedAssetIDs.Contains)) {
							// This user didn't respond in our previous round, avoid him for remaining ones
							triedSteamIDs[listedUser.SteamID] = (byte.MaxValue, previousAttempt.GivenAssetIDs, previousAttempt.ReceivedAssetIDs);

							break;
						}

						previousAttempt.GivenAssetIDs.UnionWith(itemsToGive.Select(item => item.AssetID));
						previousAttempt.ReceivedAssetIDs.UnionWith(itemsToReceive.Select(item => item.AssetID));
					} else {
						previousAttempt.GivenAssetIDs = new HashSet<ulong>(itemsToGive.Select(item => item.AssetID));
						previousAttempt.ReceivedAssetIDs = new HashSet<ulong>(itemsToReceive.Select(item => item.AssetID));
					}

					triedSteamIDs[listedUser.SteamID] = (++previousAttempt.Tries, previousAttempt.GivenAssetIDs, previousAttempt.ReceivedAssetIDs);

					emptyMatches = 0;

					Bot.ArchiLogger.LogGenericTrace(Bot.SteamID + " <- " + string.Join(", ", itemsToReceive.Select(item => item.RealAppID + "/" + item.Type + "-" + item.ClassID + " #" + item.Amount)) + " | " + string.Join(", ", itemsToGive.Select(item => item.RealAppID + "/" + item.Type + "-" + item.ClassID + " #" + item.Amount)) + " -> " + listedUser.SteamID);

					(bool success, HashSet<ulong> mobileTradeOfferIDs) = await Bot.ArchiWebHandler.SendTradeOffer(listedUser.SteamID, itemsToGive, itemsToReceive, listedUser.TradeToken, true).ConfigureAwait(false);

					if ((mobileTradeOfferIDs != null) && (mobileTradeOfferIDs.Count > 0) && Bot.HasMobileAuthenticator) {
						(bool twoFactorSuccess, _) = await Bot.Actions.HandleTwoFactorAuthenticationConfirmations(true, Steam.ConfirmationDetails.EType.Trade, mobileTradeOfferIDs, true).ConfigureAwait(false);

						if (!twoFactorSuccess) {
							Bot.ArchiLogger.LogGenericTrace(Strings.WarningFailed);

							return false;
						}
					}

					if (!success) {
						Bot.ArchiLogger.LogGenericTrace(Strings.WarningFailed);

						break;
					}

					skippedSetsThisUser.UnionWith(skippedSetsThisTrade);
					Bot.ArchiLogger.LogGenericTrace(Strings.Success);
				}

				if (skippedSetsThisUser.Count == 0) {
					if (skippedSetsThisRound.Count == 0) {
						// If we didn't find any match on clean round, this user isn't going to have anything interesting for us anytime soon
						triedSteamIDs[listedUser.SteamID] = (byte.MaxValue, null, null);
					}

					if (++emptyMatches >= MaxMatchedBotsSoft) {
						break;
					}

					continue;
				}

				skippedSetsThisRound.UnionWith(skippedSetsThisUser);

				foreach ((uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) skippedSet in skippedSetsThisUser) {
					fullState.Remove(skippedSet);
					tradableState.Remove(skippedSet);
				}

				if (Trading.IsEmptyForMatching(fullState, tradableState)) {
					// User doesn't have any more dupes in the inventory
					break;
				}

				fullState.TrimExcess();
				tradableState.TrimExcess();
			}

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ActivelyMatchingItemsRound, skippedSetsThisRound.Count));

			return skippedSetsThisRound.Count > 0;
		}

		[SuppressMessage("ReSharper", "ClassCannotBeInstantiated")]
		private sealed class ListedUser {
			internal readonly HashSet<Steam.Asset.EType> MatchableTypes = new HashSet<Steam.Asset.EType>();

#pragma warning disable 649
			[JsonProperty(PropertyName = "steam_id", Required = Required.Always)]
			internal readonly ulong SteamID;
#pragma warning restore 649

#pragma warning disable 649
			[JsonProperty(PropertyName = "trade_token", Required = Required.Always)]
			internal readonly string TradeToken;
#pragma warning restore 649

			internal float Score => GamesCount / (float) ItemsCount;

#pragma warning disable 649
			[JsonProperty(PropertyName = "games_count", Required = Required.Always)]
			private readonly ushort GamesCount;
#pragma warning restore 649

#pragma warning disable 649
			[JsonProperty(PropertyName = "items_count", Required = Required.Always)]
			private readonly ushort ItemsCount;
#pragma warning restore 649

			internal bool MatchEverything { get; private set; }

#pragma warning disable IDE0051
			[JsonProperty(PropertyName = "matchable_backgrounds", Required = Required.Always)]
			private byte MatchableBackgroundsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.ProfileBackground);

							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.ProfileBackground);

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));

							return;
					}
				}
			}
#pragma warning restore IDE0051

#pragma warning disable IDE0051
			[JsonProperty(PropertyName = "matchable_cards", Required = Required.Always)]
			private byte MatchableCardsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.TradingCard);

							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.TradingCard);

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));

							return;
					}
				}
			}
#pragma warning restore IDE0051

#pragma warning disable IDE0051
			[JsonProperty(PropertyName = "matchable_emoticons", Required = Required.Always)]
			private byte MatchableEmoticonsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.Emoticon);

							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.Emoticon);

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));

							return;
					}
				}
			}
#pragma warning restore IDE0051

#pragma warning disable IDE0051
			[JsonProperty(PropertyName = "matchable_foil_cards", Required = Required.Always)]
			private byte MatchableFoilCardsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.FoilTradingCard);

							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.FoilTradingCard);

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));

							return;
					}
				}
			}
#pragma warning restore IDE0051

#pragma warning disable IDE0051
			[JsonProperty(PropertyName = "match_everything", Required = Required.Always)]
			private byte MatchEverythingNumber {
				set {
					switch (value) {
						case 0:
							MatchEverything = false;

							break;
						case 1:
							MatchEverything = true;

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));

							return;
					}
				}
			}
#pragma warning restore IDE0051

			[JsonConstructor]
			private ListedUser() { }
		}
	}
}
