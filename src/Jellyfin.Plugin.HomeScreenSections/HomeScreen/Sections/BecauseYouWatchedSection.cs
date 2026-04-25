using Jellyfin.Extensions;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections
{
	public class BecauseYouWatchedSection : IHomeScreenSection
	{
		private const int RankedPoolSize = 24;
		private const int ReturnedPoolSize = 16;

		public string? Section => "BecauseYouWatched";

		public string? DisplayText { get; set; } = "Because You Watched";

		public int? Limit => 5;

		public string? Route => null;

		public string? AdditionalData { get; set; }

		public object? OriginalPayload => null;

		public TranslationMetadata? TranslationMetadata { get; private set; }
		
		private IUserDataManager UserDataManager { get; set; }
		private IUserManager UserManager { get; set; }
		private ILibraryManager LibraryManager { get; set; }
		private IDtoService DtoService { get; set; }
		private ICollectionManager CollectionManager { get; set; }
		private CollectionManagerProxy CollectionManagerProxy { get; set; }

		public BecauseYouWatchedSection(IUserDataManager userDataManager, IUserManager userManager, ILibraryManager libraryManager, 
			IDtoService dtoService, ICollectionManager collectionManager, CollectionManagerProxy collectionProxy)
		{
			UserDataManager = userDataManager;
			UserManager = userManager;
			LibraryManager = libraryManager;
			DtoService = dtoService;
			CollectionManager = collectionManager;
			CollectionManagerProxy = collectionProxy;
		}

		public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
		{
			User? user = userId is null || userId.Value.Equals(default)
				? null
				: UserManager.GetUserById(userId.Value);

			DtoOptions? dtoOptions = new DtoOptions 
			{ 
				Fields = new[] 
				{ 
					ItemFields.PrimaryImageAspectRatio, 
					ItemFields.MediaSourceCount
				}
			};

			VirtualFolderInfo[] folders = LibraryManager.GetVirtualFolders()
				.Where(x => x.CollectionType == CollectionTypeOptions.movies)
				.FilterToUserPermitted(LibraryManager, user);

			List<BaseItem>? recentlyPlayedMovies = folders.SelectMany(x =>
			{
				var item = LibraryManager.GetParentItem(Guid.Parse(x.ItemId), user?.Id);

				if (item is not Folder folder)
				{
					folder = LibraryManager.GetUserRootFolder();
				}

				return folder.GetItems(new InternalItemsQuery(user)
				{
					IncludeItemTypes = new[]
					{
						BaseItemKind.Movie
					},
					OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending), (ItemSortBy.Random, SortOrder.Descending) },
					Limit = 15,
					ParentId = Guid.Parse(x.ItemId ?? Guid.Empty.ToString()),
					Recursive = true,
					IsPlayed = true,
					DtoOptions = dtoOptions
				}).Items;
			}).ToList();
			
			recentlyPlayedMovies.Shuffle();
			
			List<BaseItem> pickedMovies = new List<BaseItem>();

			Queue<BaseItem> queue = new Queue<BaseItem>(recentlyPlayedMovies);
			while (pickedMovies.Count < instanceCount && queue.Count > 0)
			{
				BaseItem elementToConsider = queue.Dequeue();
				
				if (user != null)
				{
					var collections = CollectionManagerProxy.GetCollections(user)
						.Select(y => (y, y.GetChildren(user, true, null)))
						.Where(y => y.Item2
							.OfType<Movie>().Contains(elementToConsider as Movie));

					bool isPicked = false;
					foreach ((BoxSet Item, IEnumerable<BaseItem> Children) collection in collections)
					{
						if (collection.Children.OfType<Movie>().Any(y => pickedMovies?.Select(z => z.Id).Contains(y.Id) ?? true))
						{
							isPicked = true;
							break;
						}
					}

					if (isPicked)
					{
						continue;
					}
				}

				pickedMovies.Add(elementToConsider);
				yield return new BecauseYouWatchedSection(UserDataManager, UserManager, LibraryManager, DtoService, CollectionManager, CollectionManagerProxy)
				{
					AdditionalData = elementToConsider.Id.ToString(),
					DisplayText = "Because You Watched " + elementToConsider.Name,
					TranslationMetadata = new TranslationMetadata()
					{
						Type = TranslationType.Pattern,
						AdditionalContent = elementToConsider.Name
					}
				};
			}
		}

		public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
		{
			User user = UserManager.GetUserById(payload.UserId)!;
			
			DtoOptions? dtoOptions = new DtoOptions
			{
				Fields = new[]
				{
					ItemFields.PrimaryImageAspectRatio,
					ItemFields.MediaSourceCount
				},
				ImageTypes = new[]
				{
					ImageType.Thumb,
					ImageType.Backdrop,
					ImageType.Primary,
				},
				ImageTypeLimit = 1
			};

			BaseItem? item = LibraryManager.GetItemById(Guid.Parse(payload.AdditionalData ?? Guid.Empty.ToString()));
			if (item is null)
			{
				return new QueryResult<BaseItemDto>();
			}

            var config = HomeScreenSectionsPlugin.Instance?.Configuration;
			var sectionSettings = config?.SectionSettings.FirstOrDefault(x => x.SectionId == Section);
            // If HideWatchedItems is enabled for this section, set isPlayed to false to hide watched items; otherwise, include all.
            bool? isPlayed = sectionSettings?.HideWatchedItems == true ? false : null;

            List<BaseItem> similar = GetRankedSimilarMovies(item, user, dtoOptions, isPlayed);
            similar.Shuffle();
            
			return new QueryResult<BaseItemDto>(DtoService.GetBaseItemDtos(similar.Take(ReturnedPoolSize).ToArray(), dtoOptions, user));
		}

		private List<BaseItem> GetRankedSimilarMovies(BaseItem seedItem, User user, DtoOptions dtoOptions, bool? isPlayed)
		{
			VirtualFolderInfo[] folders = LibraryManager.GetVirtualFolders()
				.Where(x => x.CollectionType == CollectionTypeOptions.movies)
				.FilterToUserPermitted(LibraryManager, user);

			List<Movie> candidates = folders.SelectMany(x =>
			{
				var parentItem = LibraryManager.GetParentItem(Guid.Parse(x.ItemId), user?.Id);

				if (parentItem is not Folder folder)
				{
					folder = LibraryManager.GetUserRootFolder();
				}

				return folder.GetItems(new InternalItemsQuery(user)
				{
					IncludeItemTypes = new[]
					{
						BaseItemKind.Movie
					},
					User = user,
					IsPlayed = isPlayed,
					DtoOptions = dtoOptions,
					Recursive = true,
					ParentId = Guid.Parse(x.ItemId ?? Guid.Empty.ToString()),
				}).Items;
			}).OfType<Movie>()
				.Where(x => x.Id != seedItem.Id)
				.GroupBy(x => x.Id)
				.Select(x => x.First())
				.ToList();

			List<(BaseItem Movie, double Score)> rankedCandidates = candidates
				.Select(movie => (Movie: (BaseItem)movie, Score: CalculateSimilarityScore(seedItem, movie)))
				.OrderByDescending(x => x.Score)
				.ThenByDescending(x => (x.Movie as Movie)?.ProductionYear ?? 0)
				.ThenBy(x => x.Movie.Name)
				.ToList();

			List<BaseItem> similar = rankedCandidates
				.Where(x => x.Score > 0)
				.Take(RankedPoolSize)
				.Select(x => x.Movie)
				.ToList();

			if (similar.Count >= RankedPoolSize)
			{
				return similar;
			}

			HashSet<Guid> selectedIds = similar.Select(x => x.Id).ToHashSet();

			List<BaseItem> fallback = rankedCandidates
				.Where(x => !selectedIds.Contains(x.Movie.Id))
				.Select(x => x.Movie)
				.ToList();

			fallback.Shuffle();
			similar.AddRange(fallback.Take(RankedPoolSize - similar.Count));

			return similar;
		}

		private static double CalculateSimilarityScore(BaseItem seedItem, BaseItem candidate)
		{
			HashSet<string> seedTags = NormalizeMetadata(seedItem.Tags);
			HashSet<string> seedGenres = NormalizeMetadata(seedItem.Genres);
			HashSet<string> candidateTags = NormalizeMetadata(candidate.Tags);
			HashSet<string> candidateGenres = NormalizeMetadata(candidate.Genres);

			int sharedTags = seedTags.Intersect(candidateTags).Count();
			int sharedGenres = seedGenres.Intersect(candidateGenres).Count();

			if (sharedTags == 0 && sharedGenres == 0)
			{
				return 0;
			}

			double tagScore = CalculateMetadataScore(seedTags, candidateTags, sharedTags, 12d, 36d);
			double genreScore = CalculateMetadataScore(seedGenres, candidateGenres, sharedGenres, 6d, 18d);
			double combinedBonus = sharedTags > 0 && sharedGenres > 0 ? 8d : 0d;

			return tagScore + genreScore + combinedBonus;
		}

		private static double CalculateMetadataScore(HashSet<string> seedValues, HashSet<string> candidateValues, int sharedCount, double matchWeight, double coverageWeight)
		{
			if (sharedCount == 0 || seedValues.Count == 0 || candidateValues.Count == 0)
			{
				return 0;
			}

			double coverage = (double)sharedCount / Math.Max(seedValues.Count, candidateValues.Count);
			return (sharedCount * matchWeight) + (coverage * coverageWeight);
		}

		private static HashSet<string> NormalizeMetadata(IEnumerable<string>? values)
		{
			HashSet<string> normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			if (values == null)
			{
				return normalized;
			}

			foreach (string value in values)
			{
				if (string.IsNullOrWhiteSpace(value))
				{
					continue;
				}

				normalized.Add(value.Trim());
			}

			return normalized;
		}
		
		public HomeScreenSectionInfo GetInfo()
		{
			return new HomeScreenSectionInfo
			{
				Section = Section,
				DisplayText = DisplayText,
				AdditionalData = AdditionalData,
				Route = Route,
				Limit = Limit ?? 1,
				OriginalPayload = OriginalPayload,
				ViewMode = SectionViewMode.Landscape,
                AllowHideWatched = true
			};
		}
	}
}
