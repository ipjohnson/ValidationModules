import { defineConfig } from 'vitepress';

// Published to https://ipjohnson.github.io/ValidationModules/, so every absolute path needs the
// repository name as a base. Getting this wrong is the classic Pages failure: the site builds, the
// landing page loads, and every asset and internal link 404s.
const base = '/ValidationModules/';

export default defineConfig({
  title: 'ValidationModules',
  description:
    'Compile-time validation for .NET. Constraints become straight-line C# at build time — no ' +
    'reflection, no expression trees, no regex compiled at runtime, Native AOT safe.',
  base,
  lang: 'en-GB',
  cleanUrls: true,

  // A broken internal link should fail the build rather than ship. The pages cross-reference
  // heavily and a rename would otherwise rot links silently.
  ignoreDeadLinks: false,

  head: [
    ['link', { rel: 'icon', href: `${base}favicon.svg`, type: 'image/svg+xml' }],
    ['meta', { name: 'theme-color', content: '#0f9d76' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'ValidationModules' }],
    [
      'meta',
      {
        property: 'og:description',
        content: 'Compile-time validation for .NET. No reflection, no expression trees, AOT safe.',
      },
    ],
  ],

  themeConfig: {
    siteTitle: 'ValidationModules',

    nav: [
      { text: 'Guide', link: '/guide/getting-started', activeMatch: '/guide/' },
      { text: 'Reference', link: '/reference/diagnostics', activeMatch: '/reference/' },
      {
        text: 'NuGet',
        items: [
          { text: 'Runtime', link: 'https://www.nuget.org/packages/ValidationModules.Runtime/' },
          {
            text: 'SourceGenerator',
            link: 'https://www.nuget.org/packages/ValidationModules.SourceGenerator/',
          },
          {
            text: 'SourceGenerator.Impl',
            link: 'https://www.nuget.org/packages/ValidationModules.SourceGenerator.Impl/',
          },
        ],
      },
    ],

    sidebar: {
      '/guide/': [
        {
          text: 'Getting started',
          items: [
            { text: 'Installation', link: '/guide/getting-started' },
            { text: 'Constraints', link: '/guide/constraints' },
            { text: 'Nesting and collections', link: '/guide/nesting' },
          ],
        },
        {
          text: 'Declaring rules',
          items: [
            { text: 'Rule classes', link: '/guide/rule-classes' },
            { text: 'DataAnnotations', link: '/guide/data-annotations' },
            { text: 'Patterns and regex', link: '/guide/patterns' },
          ],
        },
        {
          text: 'Running validation',
          items: [
            { text: 'The error model', link: '/guide/errors' },
            { text: 'Registration and DI', link: '/guide/registration' },
            { text: 'Async and business rules', link: '/guide/async' },
          ],
        },
        {
          text: 'Everything else',
          items: [
            { text: 'Trimming and AOT', link: '/guide/aot' },
            { text: 'Testing', link: '/guide/testing' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'Diagnostics', link: '/reference/diagnostics' },
            { text: 'Attributes', link: '/reference/attributes' },
            { text: 'Rule builder API', link: '/reference/rules-api' },
            { text: 'Error codes', link: '/reference/codes' },
            { text: 'MSBuild properties', link: '/reference/msbuild' },
          ],
        },
      ],
    },

    socialLinks: [{ icon: 'github', link: 'https://github.com/ipjohnson/ValidationModules' }],

    search: { provider: 'local' },

    editLink: {
      pattern: 'https://github.com/ipjohnson/ValidationModules/edit/main/website/:path',
      text: 'Edit this page on GitHub',
    },

    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © Ian Johnson',
    },

    outline: [2, 3],
  },
});
