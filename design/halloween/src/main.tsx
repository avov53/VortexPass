import React from 'react';
import ReactDOM from 'react-dom/client';
import { AppsSDKUIProvider } from '@openai/apps-sdk-ui/components/AppsSDKUIProvider';
import './styles.css';
import './halloween.css';
import App from './App';
ReactDOM.createRoot(document.getElementById('root')!).render(<React.StrictMode><AppsSDKUIProvider linkComponent="a"><App /></AppsSDKUIProvider></React.StrictMode>);
