import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './auth/AuthContext';
import { RequireAuth } from './auth/RequireAuth';
import AppShell from './shell/AppShell';
import AddConnection from './features/connections/AddConnection';
import ConnectionDetail from './features/connections/ConnectionDetail';
import ConnectionsList from './features/connections/ConnectionsList';
import AddProject from './features/projects/AddProject';
import ProjectDetail from './features/projects/ProjectDetail';
import IssueDetail from './features/issues/IssueDetail';
import { IssueListRoute } from './features/issues/IssueList';
import MetricsPage from './features/insight/MetricsPage';
import ChecksPage from './features/insight/ChecksPage';
import Home from './routes/Home';
import Login from './routes/Login';
import NotFound from './routes/NotFound';
import Register from './routes/Register';
import Verify from './routes/Verify';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/register" element={<Register />} />
            <Route path="/verify" element={<Verify />} />
            <Route element={<RequireAuth />}>
              <Route element={<AppShell />}>
                <Route path="/" element={<Home />} />
                <Route path="/connections" element={<ConnectionsList />} />
                <Route path="/connections/new" element={<AddConnection />} />
                <Route path="/connections/:id" element={<ConnectionDetail />} />
                <Route path="/connections/:connectionId/projects/new" element={<AddProject />} />
                <Route path="/projects/:id" element={<ProjectDetail />} />
                <Route path="/projects/:id/issues" element={<IssueListRoute />} />
                <Route path="/projects/:id/issues/:jiraIssueId" element={<IssueDetail />} />
                <Route path="/projects/:id/metrics" element={<MetricsPage />} />
                <Route path="/projects/:id/checks" element={<ChecksPage />} />
              </Route>
            </Route>
            <Route path="/404" element={<NotFound />} />
            <Route path="*" element={<Navigate to="/404" replace />} />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
