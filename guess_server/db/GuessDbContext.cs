using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace guess_server.db
{

    public class GuessDbContext : DbContext
    {
        public DbSet<Question> questions { set; get; }
        public DbSet<Users> users { set; get; }
        public DbSet<Notice> notices { set; get; }
        public DbSet<Admin> admins { set; get; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=question.db");
        }
    }

    public class Question
    {
        public int id { set; get; }
        public string name { set; get; }
    }

    public class Users
    {
        public int id { set; get; }
        public string key { set; get; }
        public string name { set; get; }
        public int correct { set; get; }
        public int score { set; get; }
        public string create { set; get; }
    }

    public class Notice
    {
        public int id { set; get; }
        public string title { set; get; }
        public string content { set; get; }
        public string author { set; get; }
        public int publish { set; get; }
        public int expire { set; get; }
        public string create { set; get; }
    }

    public class Admin
    {
        public int id { set; get; }
        public string name { set; get; }
        public string password { set; get; }
        public int super { set; get; }
    }
}
