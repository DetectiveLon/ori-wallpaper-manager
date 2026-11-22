# Provenance and clean-room record

Oriole is a new implementation authored for the `DetectiveLon` project.

- No Muralis source file, code fragment, resource, icon, screenshot, XAML, project file, or test was copied into this repository.
- The earlier Muralis review informed the feature requirements and identified failure classes to avoid (timer correctness, fullscreen races, credential handling, response-size limits, and unclear source configuration). Those observations are design input, not reused implementation.
- Wallhaven integration is based on the provider's public API documentation: https://wallhaven.cc/help/api
- Framework documentation: https://dotnet.microsoft.com/en-us/platform/support/policy
- No third-party runtime package is currently referenced by the application itself. Test packages are separately identified in the test project and retain their own licences.

The current code is generated as project output for the user. The current OpenAI Terms of Use state that, as between the user and OpenAI and to the extent permitted by law, the user owns Output and OpenAI assigns any rights it may have in that Output. That does not transfer rights in third-party services, third-party content, or dependencies. See https://openai.com/policies/terms-of-use/.

This note is a provenance record, not legal advice or a trademark clearance.
