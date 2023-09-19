﻿using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Robust.UnitTesting.Shared.Spawning;

[TestFixture]
public sealed class SpawnInContainerOrDropTest : EntitySpawnHelpersTest
{
    [Test]
    public async Task Test()
    {
        await Setup();

        // Spawning next to an entity in a container will insert the entity into the container.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildA, "greatGrandChildA");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(GrandChildA));
            Assert.That(Container.IsEntityInContainer(uid));
            Assert.That(Container.GetContainer(GrandChildA, "greatGrandChildA").Contains(uid));
        });

        // The container is now full, spawning will insert into the outer container.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildA, "greatGrandChildA");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(ChildA));
            Assert.That(Container.IsEntityInContainer(uid));
            Assert.That(Container.GetContainer(ChildA, "grandChildA").Contains(uid));
        });

        // If outer two containers are full, will insert into outermost container.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildA, "greatGrandChildA");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(Parent));
            Assert.That(Container.IsEntityInContainer(uid));
            Assert.That(Container.GetContainer(Parent, "childA").Contains(uid));
        });

        // Finally, this will drop the item on the map.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildA, "greatGrandChildA");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(Map));
            Assert.That(Container.IsEntityInContainer(uid), Is.False);
            Assert.That(EntMan.GetComponent<TransformComponent>(uid).Coordinates, Is.EqualTo(ParentPos));
        });

        // Repeating this will just drop it on the map again.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildA, "greatGrandChildA");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(Map));
            Assert.That(Container.IsEntityInContainer(uid), Is.False);
            Assert.That(EntMan.GetComponent<TransformComponent>(uid).Coordinates, Is.EqualTo(ParentPos));
        });

        // Repeat the above but with the B-children. As _grandChildB is not actually IN a container, entities will
        // simply be parented to _childB.

        // First insert works fine
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildB, "greatGrandChildB");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(GrandChildB));
            Assert.That(Container.IsEntityInContainer(uid));
            Assert.That(Container.GetContainer(GrandChildB, "greatGrandChildB").Contains(uid));
        });

        // Second insert will drop the entity next to _grandChildB
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildB, "greatGrandChildB");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(ChildB));
            Assert.That(Container.IsEntityInContainer(uid), Is.False);
            Assert.That(EntMan.GetComponent<TransformComponent>(uid).Coordinates, Is.EqualTo(GrandChildBPos));
        });

        // Repeating this will just repeat the above behaviour.
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildB, "greatGrandChildB");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(ChildB));
            Assert.That(Container.IsEntityInContainer(uid), Is.False);
            Assert.That(EntMan.GetComponent<TransformComponent>(uid).Coordinates, Is.EqualTo(GrandChildBPos));
        });

        // Trying to spawning inside a non-existent container just drops the entity
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, GrandChildB, "foo");
            Assert.That(EntMan.EntityExists(uid));
            Assert.That(Xforms.GetParentUid(uid), Is.EqualTo(ChildB));
            Assert.That(Container.IsEntityInContainer(uid), Is.False);
            Assert.That(EntMan.GetComponent<TransformComponent>(uid).Coordinates, Is.EqualTo(GrandChildBPos));
        });

        // Trying to spawning "inside" a map just drops the entity in nullspace
        await Server.WaitPost(() =>
        {
            var uid = EntMan.SpawnInContainerOrDrop(null, Map, "foo");
            Assert.That(EntMan.EntityExists(uid));
            var xform = EntMan.GetComponent<TransformComponent>(uid);
            Assert.That(xform.ParentUid, Is.EqualTo(EntityUid.Invalid));
            Assert.That(xform.MapID, Is.EqualTo(MapId.Nullspace));
            Assert.Null(xform.MapUid);
            Assert.Null(xform.GridUid);
        });

        await Server.WaitPost(() =>MapMan.DeleteMap(MapId));
        Server.Dispose();
    }
}
