import { PluginSDK, LoggerLevel } from '@logitech/plugin-sdk';
import { ALL_ACTIONS } from './src/actions/index.js';

const sdk = new PluginSDK({ logLevel: LoggerLevel.INFO });

for (const ActionClass of ALL_ACTIONS) {
  sdk.registerAction(new ActionClass());
}

await sdk.connect();
console.log(`[NeonDeck] connected, ${ALL_ACTIONS.length} actions registered`);
