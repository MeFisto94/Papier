# Papier - A .NET Application patching tool
The aim of Papier (german for `paper`) is to simplify working with compiled 
.NET Assembly without having source code access.

Working with means to make changes to the files as if they were an open source 
project and allowing to persist these changes across (upstream-)
updates in a maintainable manner.

This is achieved by allowing the user to work with the decompiled sourcecode.
The user then commits the changes to a virtual git repository, which is the
key working principle here. This repository can be treated like a dedicated
repository, including rebasing, branching and merging(*).

In order to be able to share those repositories openly, the repository is 
converted into a series of git diff patch files. Those do not include the
initial decompilation and thus only include potential copyright protected
content as far as required by git to establish a context (reference work).

_Disclaimer_: I am no lawyer and this is especially no legal advice. This 
system most probably touches a grey area and it may always be a good idea
to ask for permission before hosting the patches. Furthermore decompilation
or working with copyrighted content may be subject to local laws.


__*__ Currently different branches are not yet supported, due to a lack of
instructions on how to convert them to patches.

### Getting Started
Using Papier is relatively simple, mostly due to a lack of command line
options and features, though.

Papier basically provides four operations at the moment:
- `sln`, which only decompiles the project and creates a `csproj`
(sln file generation is currently broken). That allows you to inspect the 
original codebase. This is especially helpful because the IDE works best
with that mode, things like `Find Usages` otherwise won't find usages
within the DLL, but only within your code.
- `build`, which compiles an existing project into the dll. This is what
you use if you're in the mid of making changes. This does not alter the
patches or the repositories, because those could otherwise overwrite your
progress! It requires an existing repository, though, so when starting 
clean use `applyPatches`
- `applyPatches`, which decompiles the `Assembly-CSharp.dll` from
`work/BuildData` (where all the other referenced DLLs including the .net
framework (`mscorlib.dll`, `System.X.dll`) need to reside) and then applies
the patches from `patches/Assembly-CSharp` (or doing nothing if those
aren't present (i.e. starting clean)) into the `repos/Assembly-CSharp`
folder.   
**WARNING**: This _will_ overwrite the contents of the repository folder
and thus delete _all_ changes made to the repository (code and git wise).
Typically you use this command after a `rebuildPatches` to verify everything
works as expected or after a clean checkout/starting a new project.
- `rebuildPatches`, which cleans the `patches/Assembly-CSharp` folder and then
rebuilds all patch files with contents from `repos/Assembly-CSharp`, so you
typically run this after you have committed (or amended) changes and before
committing the patch changes to the "real" repository, where development is
happening.

#### TLDR
- Fresh project/checkout -> `applyPatches`
- Want to inspect the sourcecode (helpful as addition) -> `sln`
- Made changes, want to build? -> `build`
- You have made your changes and committed them to the virtual repo? 
-> `rebuildPatches`

And that's also how a regular workflow would look like: Applying, creating 
a solution, editing the code, building and debugging in a loop and then 
committing changes to the virtual repo, rebuilding the patches, committing
the patches to the real repository and then starting with applying again.

### How it works in Detail
The bigger picture has already been outlined, so this is mostly related for
those curious or potentially even looking into contributing! Contributions
are very welcome, especially given the fact that a lot of things may 
just work for one project and not for others, so generalizing Papier is
much needed.

I'll first draw the picture of how this would work with java, before going
into all the .NET pitfalls present.

Essentially, when the user is done editing the source files, we recompile
those that have been changed (the user needs to specify which files to 
"import" beforehand for this reason). This is because we don't want to 
recompile the whole source code as that may have decompilation errors 
and other weirdness (just not producing the same bytecode), which increase
the binary diff that needs to be kept small for better deployment.

For Java, the story would end there: We would just compile a few source
files and put the old jar on the classpath (maybe stripping the classes
that are replaced) and the copy the resulting classes into the jar again.

Unfortunately for .NET an Assembly is more complicated than just an
archive of binary (IL) files. Classes are actually encapsulated into
Assemblies. That means, a class with the exact same namespace and name
can exist in multiple assemblies. That may trigger a warning, but is
possible.

This becomes a problem when we try to compile a snippet like:
```c#
class A {
    void a() {
        B.b(this);
    }
}
```
The compiler will warn that A is defined multiple times and throw an error
that it is unable to convert the type `A` into `A` (of different assemblies).
Another problem is that we can't simply drop `A` from the original application
assembly, because then `B#b` is a method with unknown/unresolvable parameters,
as the type `A` is not defined within that assembly or the references.

What we do instead, is _renaming_ the type by appending a `_hidden` to it's
name. That way the names don't clash anymore, but the compiler would still
not like the above class, because `A_hidden != A`.

This is where stubbing comes in. Fortunately, the issue only happens whenever
a class of the source set (any class that has been imported, manually or 
implicitly) leaks it's type into a referenced type. That means, had we called
`B.b(new C());`, whereby both B and C are not in the sourcecode, the call 
wouldn't be a problem.

My solution to that is providing a prototype-only class stub and adding it to
the source-set. That way, the compiler is seeing both `A` and `B` in the
same assembly and the call succeeds. To achieve this, the classes that need 
stubbing need to be determined and then the _members_ that need stubbing are
determined. The latter happens to reduce the (useless) workload for the 
compiler and also reduces the chance of exposing a bug in our stub generation ;)
The `StubGenerator` inspects the assembly and tries to re-assemble prototype
classes much like a decompiler would. The risk here is not catching a
callsite, which would mean the stub lacks a member, that cannot be resolved.
Currently we almost exclusively look for method calls from method bodies,
but there are potential corner cases.

Example generated stub for B:
```c#
[PapierStub]
class B {
   void b(NameSpace.Of.A a) {}
}
```

Now it's important to know, that the renaming step done above would change
all references to `A` to point to `A_hidden`, which is why that assembly is
really only used for the compiler as reference and not in the final product.
So we now have the original assembly again and the `_diff.dll`, which
contains the compiler output (i.e. source set and stubs).

Since DLLs aren't simple zip files, we need yet another tool (ILMerge/ILRepack)
that is designed to merge two assemblies into one, but more so with the purpose
of reducing the number of dependency DLLs. That's also why types need to be
whitelisted to be duplicate (which is what we want for the source set).
Furthermore it currently only provides the possibility to use Attributes to
drop (blacklist) types (see `PapierStub` on the stub above). We also use that
to optionally annotate the source set types in the original DLL, even though
I think that is not required, because the tool won't "properly" merge the
types, unless `UnionMerge` is set (that would prevent us from being able
to delete methods, though).

### License
The project is licensed under the MIT License.

### Credits
This system is based on the amazing work of the PaperMC Community, see 
[Paper](https://github.com/PaperMC/Paper) for the spiritual Java predecessor
of Papier. They use the patch system to provide a highly customized Minecraft
Dedicated Server Implementation and even fix upstream bugs.