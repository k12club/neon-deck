import { unlinkPlugin, postBuildProcessing } from '@logitech/plugin-toolkit';
import { esmShimPlugin } from '@logitech/plugin-toolkit/esbuild';
import { build, context } from 'esbuild';
import { dirname, resolve } from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const isProduction = process.env.NODE_ENV === 'production';

// Track if we're in watch mode
let isWatchMode = false;

const config = {
  entryPoints: [resolve(__dirname, 'index.js')],
  bundle: true,
  outfile: resolve(__dirname, 'dist/', 'index.mjs'),
  platform: 'node',
  target: 'es2022',
  format: 'esm',
  minify: isProduction,
  define: {
    'process.env.NODE_ENV': JSON.stringify(process.env.NODE_ENV || 'development')
  },
  logLevel: 'info',
  plugins: [
    esmShimPlugin({ require: true, globals: true }),
    {
      name: 'post-build',
      setup(build) {
        build.onEnd(async (result) => {
          if (result.errors.length === 0) {
            try {
              await postBuildProcessing(__dirname, isWatchMode); // Link only in watch mode
              if (isWatchMode) {
                console.log('👀 Watching for file changes... Press Ctrl+C to stop.');
              } else {
                console.log('✅ Build completed successfully');
              }
            } catch (error) {
              console.error('❌ Post-build processing failed:', error.message);
            }
          } else {
            console.error('❌ Build failed with errors:', result.errors);
          }
        });
      }
    }
  ]
};

// Build function
async function buildPlugin() {
  try {
    isWatchMode = false;
    await build(config);
  } catch (error) {
    console.error('❌ Build failed:', error);
    process.exit(1);
  }
}

// Watch function
async function watchPlugin() {
  try {
    isWatchMode = true;

    // Create esbuild context for watching
    const watchConfig = Object.assign({}, config, { logLevel: 'error' });
    const ctx = await context(watchConfig);

    // Start watching
    await ctx.watch();
    console.log('👀 Watching for file changes... Press Ctrl+C to stop.');

    // Handle graceful shutdown
    const cleanup = async () => {
      console.log('\n🛑 Stopping watch mode...');
      try {
        console.log('🔓 Unlinking plugin...');
        await unlinkPlugin(true);
      } catch (error) {
        console.warn('⚠️ Failed to unlink plugin:', error.message);
      }
      await ctx.dispose();
      process.exit(0);
    };

    process.on('SIGINT', cleanup);
    process.on('SIGTERM', cleanup);

  } catch (error) {
    console.error('❌ Watch mode failed:', error);
    process.exit(1);
  }
}

// Export config and functions
export { buildPlugin, watchPlugin, config };

// Run build or watch if this file is executed directly
const currentFileUrl = import.meta.url;
const executedFileUrl = new URL(process.argv[1], 'file://').href;

// Also check if the filename matches (more robust across platforms)
const currentFileName = fileURLToPath(currentFileUrl);
const executedFileName = process.argv[1];

const isDirectExecution = currentFileUrl === executedFileUrl ||
  currentFileName.endsWith(executedFileName) ||
  (process.argv[1] && process.argv[1].endsWith('esbuild.config.js'));

if (isDirectExecution) {
  // Check for watch flag
  const watchFlag = process.argv.includes('--watch') || process.argv.includes('-w');

  if (watchFlag) {
    watchPlugin();
  } else {
    buildPlugin();
  }
}
