using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace MediaInfoKeeper.Common
{
    public static class LibraryApi
    {
        private static IUserManager userManager;

        public static Dictionary<User, bool> AllUsers { get; private set; } =
            new Dictionary<User, bool>();

        public static string[] AdminOrderedViews { get; private set; } = Array.Empty<string>();

        public static void Initialize(IUserManager manager)
        {
            userManager = manager;
            FetchUsers();
        }

        public static void FetchUsers()
        {
            if (userManager == null)
            {
                return;
            }

            try
            {
                var users = userManager.GetUserList(new UserQuery()) ?? Array.Empty<User>();
                AllUsers = users.ToDictionary(u => u, u => u.Policy?.IsAdministrator == true);
                AdminOrderedViews = users.FirstOrDefault(u => u.Policy?.IsAdministrator == true)
                    ?.Configuration?.OrderedViews ?? AdminOrderedViews;
            }
            catch
            {
                AllUsers = new Dictionary<User, bool>();
                AdminOrderedViews = Array.Empty<string>();
            }
        }

        public static string[] FetchAdminOrderedViews()
        {
            FetchUsers();
            return AdminOrderedViews;
        }
    }
}
