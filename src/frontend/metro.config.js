const { getDefaultConfig } = require('expo/metro-config');
const config = getDefaultConfig(__dirname);
// MSAL publishes its browser entry through package exports, not the legacy module field.
config.resolver.unstable_enablePackageExports = true;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  if (moduleName === '@azure/msal-common/browser') {
    return { filePath: require.resolve('@azure/msal-common/browser'), type: 'sourceFile' };
  }
  return context.resolveRequest(context, moduleName, platform);
};
module.exports = config;
