﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Data.Entity.ModelConfiguration.Conventions;
using System.Linq;
using d60.EventSorcerer.Events;
using d60.EventSorcerer.Extensions;
using d60.EventSorcerer.Views.Basic;

namespace d60.EventSorcerer.EntityFramework
{
    public class EntityFrameworkViewManager<TView> : IViewManager where TView : class,IView, ISubscribeTo, new()
    {
        private readonly string _connectionStringOrName;

        private readonly ViewDispatcherHelper<TView> _dispatcherHelper = new ViewDispatcherHelper<TView>();
        private int _maxDomainEventsBetweenFlush;

        public EntityFrameworkViewManager(string connectionStringOrName, bool createDatabaseIfnotExist = true)
        {
            _connectionStringOrName = connectionStringOrName;


            Database.SetInitializer(new CreateDatabaseIfNotExists<GenericViewContext<TView>>());

            if (createDatabaseIfnotExist)
            {

                using (var context = new GenericViewContext<TView>(_connectionStringOrName))
                {
                    //touch tables to create them
                    context.ViewCollection.FirstOrDefault();
                    context.Configurations.FirstOrDefault();
                }
            }

        }

        public int MaxDomainEventsBetweenFlush
        {
            get { return _maxDomainEventsBetweenFlush; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentException(string.Format("Attempted to set max events between flush to {0}, but it must be greater than 0!", value));
                }
                _maxDomainEventsBetweenFlush = value;
            }
        }

        public void Initialize(IViewContext context, IEventStore eventStore, bool purgeExistingViews = false)
        {
            using (var dbContext = new GenericViewContext<TView>(_connectionStringOrName))
            {
                var sql = "DELETE FROM " + dbContext.ViewTableName;

                dbContext.Database.ExecuteSqlCommand(sql);


                var entityFrameworkConfiguration = dbContext.Configurations.Find(dbContext.ViewInstanceId);
                if (entityFrameworkConfiguration != null)
                    dbContext.Configurations.Remove(entityFrameworkConfiguration);

                dbContext.SaveChanges();
            }


            var lastSeenMaxGlobalSequenceNumber = FindMax();

            foreach (var batch in eventStore.Stream(lastSeenMaxGlobalSequenceNumber + 1).Batch(MaxDomainEventsBetweenFlush))
            {
                var eventsList = batch.ToList();

                using (var genericViewBasse = new GenericViewContext<TView>(_connectionStringOrName))
                {
                    foreach (var e in eventsList)
                    {
                        DispatchEvent(e, genericViewBasse, context);
                    }

                    UpdateMax(eventsList.Last().GetGlobalSequenceNumber(), genericViewBasse);

                    SaveChanges(genericViewBasse);
                }
            }
        }

        public void Dispatch(IViewContext context, IEventStore eventStore, IEnumerable<DomainEvent> events)
        {
            var eventsList = events.ToList();

            using (var genericViewBasse = new GenericViewContext<TView>(_connectionStringOrName))
            {
                foreach (var e in eventsList)
                {
                    DispatchEvent(e, genericViewBasse, context);
                }
                SaveChanges(genericViewBasse);
                UpdateMax(eventsList.Last().GetGlobalSequenceNumber(), genericViewBasse);

                SaveChanges(genericViewBasse);
            }
        }

        private static void SaveChanges(GenericViewContext<TView> genericViewBasse)
        {
            try
            {
                genericViewBasse.SaveChanges();
            }
            catch (System.Data.Entity.Validation.DbEntityValidationException entityValidationException)
            {
                foreach (var error in entityValidationException.EntityValidationErrors)
                {
                    Console.WriteLine("entry:" + error.Entry);

                    foreach (var valError in error.ValidationErrors)
                    {
                        Console.WriteLine(String.Format("Property: {0} has error: {1}", valError.PropertyName, valError.ErrorMessage));
                    }
                }
            }
            //catch (System.Data.Entity.Infrastructure.DbUpdateException updateException)
            //{
            //    foreach (var dbEntityEntry in updateException.Entries)
            //    {
            //        Console.WriteLine(dbEntityEntry.Entity.GetType().Name);
            //    }

            //    throw;
            //}
        }

        private void DispatchEvent(DomainEvent domainEvent, GenericViewContext<TView> genericViewBasse, IViewContext context)
        {
            var locator = ViewLocator.GetLocatorFor<TView>();
            var id = locator.GetViewId(domainEvent);

            var instance = genericViewBasse.ViewCollection.Find(id)
                           ?? CreateAndAddNewViewInstance(genericViewBasse, id);

            _dispatcherHelper.DispatchToView(context, domainEvent, instance);
        }

        private TView CreateAndAddNewViewInstance(GenericViewContext<TView> genericViewBasse, string id)
        {
            var instance = new TView();
            instance.Id = id;
            genericViewBasse.ViewCollection.Add(instance);
            return instance;
        }

        private long FindMax()
        {
            using (var context = new GenericViewContext<TView>(_connectionStringOrName))
            {
                var instance = context.Configurations.AsNoTracking().FirstOrDefault(c => c.Id == typeof(TView).FullName);

                return instance == null ? -1 : instance.GlobalSequenceNumber;


            }
        }

        private void UpdateMax(long newMax, GenericViewContext<TView> context)
        {
            var instance = context.Configurations.FirstOrDefault(c => c.Id == typeof(TView).FullName);

            if (instance == null)
            {
                context.Configurations.Add(new EntityFrameworkConfiguration() { Id = context.ViewInstanceId, GlobalSequenceNumber = newMax });
            }
            else
            {
                instance.GlobalSequenceNumber = newMax;
            }
        }

        class GenericViewContext<TView> : DbContext where TView : class, IView
        {
            public GenericViewContext(string connectionstringOrName)
                : base(connectionstringOrName)
            {
            }

            public DbSet<TView> ViewCollection { get; set; }

            public DbSet<EntityFrameworkConfiguration> Configurations { get; set; }
            public string ViewTableName = typeof(TView).Name;
            public string ViewConfigName = typeof(TView).Name + "Configs";
            public string ViewInstanceId = typeof(TView).FullName;
            protected override void OnModelCreating(DbModelBuilder modelBuilder)
            {
                modelBuilder.Entity<TView>().ToTable(ViewTableName);
                modelBuilder.Entity<TView>().HasKey(v => v.Id);

                modelBuilder.Entity<EntityFrameworkConfiguration>().ToTable(ViewConfigName);

                base.OnModelCreating(modelBuilder);
            }
        }

        public TView Load(string id)
        {
            using (var context = new GenericViewContext<TView>(_connectionStringOrName))
            {
                return context.ViewCollection.AsNoTracking().FirstOrDefault(v => v.Id == id);
            }
        }
    }

    class EntityFrameworkConfiguration
    {
        [Key]
        public string Id { get; set; }
        [Required]
        public long GlobalSequenceNumber { get; set; }
    }
}
