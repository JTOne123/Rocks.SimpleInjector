﻿namespace Rocks.SimpleInjector.Tests.NonThreadSafeCheck.TestModels
{
    internal class SutWithReadonlyValueTypeField
    {
#pragma warning disable 169
        private readonly string member;
#pragma warning restore 169
    }
}