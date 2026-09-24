import { defineConfig } from 'vitepress';

// Published to https://ipjohnson.github.io/ValidationModules/. Every absolute path needs the
// repository name as its base, or the assets and internal links 404 on GitHub Pages.
const base = '/ValidationModules/';

const description =
  'Compile-time validation for .NET. A source generator writes the validators, and they run under Native AOT.';

export default defineConfig({
  title: 'ValidationModules',
  description,
  base,
  lang: 'en-GB',
  cleanUrls: true,

  // A broken internal link fails the build.
  ignoreDeadLinks: false,

  head: [
    ['link', { rel: 'icon', href: `${base}favicon.svg`, type: 'image/svg+xml' }],
    ['meta', { name: 'theme-color', content: '#0f9d76' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'ValidationModules' }],
    ['meta', { property: 'og:description', content: description }],
  ],

  themeConfig: {
    logo: { light: '/logo.svg', dark: '/logo-dark.svg' },

    nav: [
      { text: 'Guide', link: '/guide/getting-started', activeMatch: '/guide/' },
      { text: 'Reference', link: '/reference/attributes', activeMatch: '/reference/' },
      {
        text: 'Packages',
        items: [
          { text: 'ValidationModules.Runtime', link: 'https://www.nuget.org/packages/ValidationModules.Runtime/' },
          {
            text: 'ValidationModules.SourceGenerator',
            link: 'https://www.nuget.org/packages/ValidationModules.SourceGenerator/',
          },
          { text: 'ValidationModules.AspNetCore', link: 'https://www.nuget.org/packages/ValidationModules.AspNetCore/' },
          { text: 'ValidationModules.Options', link: 'https://www.nuget.org/packages/ValidationModules.Options/' },
          { text: 'ValidationModules.Messages', link: 'https://www.nuget.org/packages/ValidationModules.Messages/' },
          {
            text: 'ValidationModules.SourceGenerator.Impl',
            link: 'https://www.nuget.org/packages/ValidationModules.SourceGenerator.Impl/',
          },
        ],
      },
      { text: 'Releases', link: 'https://github.com/ipjohnson/ValidationModules/releases' },
    ],

    sidebar: {
      '/guide/': [
        {
          text: 'Start',
          items: [
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'How it works', link: '/guide/how-it-works' },
          ],
        },
        {
          text: 'Declaring rules',
          items: [
            { text: 'Constraint attributes', link: '/guide/constraints' },
            { text: 'Rules classes', link: '/guide/rule-classes' },
            { text: 'Nested objects and collections', link: '/guide/nesting' },
            { text: 'Patterns', link: '/guide/patterns' },
            { text: 'Custom constraints', link: '/guide/custom-constraints' },
          ],
        },
        {
          text: 'Running validation',
          items: [
            { text: 'Results and errors', link: '/guide/errors' },
            { text: 'Messages and languages', link: '/guide/messages' },
            { text: 'Registration', link: '/guide/registration' },
            { text: 'Async validation', link: '/guide/async' },
            { text: 'Testing', link: '/guide/testing' },
          ],
        },
        {
          text: 'Integrations',
          items: [
            { text: 'ASP.NET Core', link: '/guide/aspnetcore' },
            { text: 'Options', link: '/guide/options' },
            { text: 'DataAnnotations', link: '/guide/data-annotations' },
            { text: 'Native AOT', link: '/guide/aot' },
          ],
        },
        {
          text: 'More',
          items: [
            { text: 'Coming from FluentValidation', link: '/guide/fluentvalidation' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'Attributes', link: '/reference/attributes' },
            { text: 'Rules API', link: '/reference/rules-api' },
            { text: 'Validation codes', link: '/reference/codes' },
            { text: 'Diagnostics', link: '/reference/diagnostics' },
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
