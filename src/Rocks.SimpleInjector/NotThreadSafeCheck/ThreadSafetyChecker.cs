﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Rocks.Helpers;
using Rocks.SimpleInjector.Attributes;
using Rocks.SimpleInjector.NotThreadSafeCheck.Models;
using SimpleInjector;

namespace Rocks.SimpleInjector.NotThreadSafeCheck
{
    /// <summary>
    ///     <para>
    ///         A class that can check <see cref="Type" /> for potential not thread safe members.
    ///         This is not guarantees that the class is safe or not - it's just helps to find
    ///         probable unitentional usage of the type (for example, singleton registration
    ///         in dependency injection container).
    ///     </para>
    ///     <para>
    ///         This class heavily uses reflection so it's very performant and should be used
    ///         mostly in unit tests or uppon application startup.
    ///     </para>
    /// </summary>
    // ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
    public class ThreadSafetyChecker
    {
        protected const BindingFlags DefaultBindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly |
                                                           BindingFlags.Instance | BindingFlags.Static;

        protected readonly Dictionary<Type, ThreadSafetyCheckResult> cache;
        protected readonly InstanceProducer[] registrations;


        /// <summary>
        ///     Initializes a new instance of the <see cref="T:System.Object" /> class.
        /// </summary>
        public ThreadSafetyChecker([NotNull] Container container)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            this.registrations = container.GetCurrentRegistrations();
            this.cache = new Dictionary<Type, ThreadSafetyCheckResult>();

            this.KnownNotMutableTypes = new List<Type>
                                        {
                                            typeof(string),
                                            typeof(IEnumerable),
                                            typeof(IEnumerable<>),
                                            typeof(IReadOnlyCollection<>),
                                            typeof(IReadOnlyList<>),
                                            typeof(IReadOnlyDictionary<,>),
                                            typeof(Regex)
                                        };
        }


        /// <summary>
        ///     A list of reference types that are considered not mutable.
        ///     By default includes:
        ///     <see cref="string" />,
        ///     <see cref="IEnumerable" />,
        ///     <see cref="IEnumerable{T}" />,
        ///     <see cref="IReadOnlyCollection{T}" />,
        ///     <see cref="IReadOnlyList{T}" />,
        ///     <see cref="IReadOnlyDictionary{TKey,TValue}" />,
        ///     <see cref="Regex" />.
        /// </summary>
        public IList<Type> KnownNotMutableTypes { get; set; }


        /// <summary>
        ///     Gets a list of potential non thread members of the type.
        /// </summary>
        [NotNull]
        public IReadOnlyList<NotThreadSafeMemberInfo> Check([NotNull] Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var result = this.CheckInternal(type);

            // ReSharper disable once AssignNullToNotNullAttribute
            return result.NotThreadSafeMembers.ConvertToReadOnlyList();
        }


        /// <summary>
        ///     Clears internal cache of checked types.
        /// </summary>
        public void ClearCache()
        {
            this.cache.Clear();
        }


        protected virtual ThreadSafetyCheckResult CheckInternal([NotNull] Type type)
        {
            if (this.IsNotMutableType(type))
                return ThreadSafetyCheckResult.Safe;

            ThreadSafetyCheckResult result;
            if (!this.cache.TryGetValue(type, out result))
            {
                // mark as "checking started" to prevent infinite recursion
                this.cache[type] = null;

                result = new ThreadSafetyCheckResult();

                var events = this.GetAllEvents(type);

                foreach (var not_thread_safe_member in this.GetAllFields(type)
                                                           .Where(field => !events.Any(x => x.Name.Equals(field.Name, StringComparison.Ordinal)))
                                                           .Select(this.CheckField)
                                                           .Concat(this.GetAllProperties(type).Select(this.CheckProperty))
                                                           .Concat(events.Select(this.CheckEvent))
                                                           .SkipNull())
                {
                    if (ReferenceEquals(not_thread_safe_member, NotThreadSafeMemberInfo.PotentiallySafe))
                        result.NotFullyChecked = true;
                    else
                        result.NotThreadSafeMembers.Add(not_thread_safe_member);
                }

                this.cache[type] = result;
            }
            else if (result == null)
            {
                // cyclic reference checking
                return new ThreadSafetyCheckResult { NotFullyChecked = true };
            }

            return result;
        }


        [CanBeNull]
        protected virtual NotThreadSafeMemberInfo CheckField(FieldInfo field)
        {
            if (field.IsLiteral || this.IsCompilerGenerated(field) || ThreadSafeAttribute.ExsitsOn(field))
                return null;

            if (!field.IsInitOnly)
                return new NotThreadSafeMemberInfo(field, ThreadSafetyViolationType.NonReadonlyMember);

            var result = this.CheckMember(field, field.FieldType);

            return result;
        }


        [CanBeNull]
        protected virtual NotThreadSafeMemberInfo CheckProperty(PropertyInfo property)
        {
            if (this.IsCompilerGenerated(property) || ThreadSafeAttribute.ExsitsOn(property))
                return null;

            if (property.CanWrite)
                return new NotThreadSafeMemberInfo(property, ThreadSafetyViolationType.NonReadonlyMember);

            var result = this.CheckMember(property, property.PropertyType);

            return result;
        }


        [CanBeNull]
        protected virtual NotThreadSafeMemberInfo CheckEvent(EventInfo e)
        {
            if (this.IsCompilerGenerated(e) || ThreadSafeAttribute.ExsitsOn(e))
                return null;

            var result = new NotThreadSafeMemberInfo(e, ThreadSafetyViolationType.EventFound);

            return result;
        }


        [CanBeNull]
        protected virtual NotThreadSafeMemberInfo CheckMember(MemberInfo member, Type memberType)
        {
            if (this.IsNotMutableType(memberType) || this.HasSingletonRegistration(memberType))
                return null;

            if (this.HasNotSingletonRegistration(memberType))
                return new NotThreadSafeMemberInfo(member, ThreadSafetyViolationType.NonSingletonRegistration);

            if (memberType == typeof(object))
                return new NotThreadSafeMemberInfo(member, ThreadSafetyViolationType.MutableReadonlyMember);

            var check_result = this.CheckInternal(memberType);

            if (!check_result.NotThreadSafeMembers.IsNullOrEmpty())
                return new NotThreadSafeMemberInfo(member, ThreadSafetyViolationType.MutableReadonlyMember);

            if (check_result.NotFullyChecked)
                return NotThreadSafeMemberInfo.PotentiallySafe;

            return null;
        }


        protected virtual bool IsNotMutableType(Type type)
        {
            if (type.IsValueType)
                return true;

            if (this.KnownNotMutableTypes.Any(t => t == type ||
                                                   (t.IsGenericTypeDefinition &&
                                                    type.IsGenericType &&
                                                    type.GetGenericTypeDefinition() == t &&
                                                    this.IsNotMutableGenericType(type))))
                return true;

            return false;
        }


        protected virtual bool IsNotMutableGenericType(Type type)
        {
            return type.GenericTypeArguments.All(this.IsNotMutableType);
        }


        protected virtual bool IsCompilerGenerated(MemberInfo member)
        {
            return member.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0;
        }


        protected virtual bool HasNotSingletonRegistration(Type type)
        {
            var result = this.registrations.Any(x => x.ServiceType == type && x.Lifestyle != Lifestyle.Singleton);

            return result;
        }


        protected virtual bool HasSingletonRegistration(Type type)
        {
            var result = this.registrations.Any(x => x.ServiceType == type && x.Lifestyle == Lifestyle.Singleton);

            return result;
        }


        protected virtual List<FieldInfo> GetAllFields(Type type)
        {
            var result = type.GetFields(DefaultBindingFlags).ToList();

            if (type.BaseType != null)
                result.AddRange(this.GetAllFields(type.BaseType));

            return result;
        }


        protected virtual List<EventInfo> GetAllEvents(Type type)
        {
            var result = type.GetEvents(DefaultBindingFlags).ToList();

            if (type.BaseType != null)
                result.AddRange(this.GetAllEvents(type.BaseType));

            return result;
        }


        protected virtual List<PropertyInfo> GetAllProperties(Type type)
        {
            var result = type.GetProperties(DefaultBindingFlags).ToList();

            if (type.BaseType != null)
                result.AddRange(this.GetAllProperties(type.BaseType));

            return result;
        }
    }
}